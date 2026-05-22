using System.Reflection;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// End-to-end upgrade-path coverage for <see cref="DbContext.TryMigrateForRead"/>.
/// Seeds a DB with the legacy symbol schema (no start_line / end_line / signature / etc.),
/// then opens it through the normal read path so the opportunistic migration runs and
/// leaves the newly added columns as NULL — reproducing the real-world #58 failure mode.
/// レガシー symbol スキーマから read path 経由でアップグレードするシナリオを検証する。
/// </summary>
[Collection("SQLite pool sensitive")]
public class LegacySchemaMigrationTests : IDisposable
{
    private readonly string _dbDir;
    private readonly string _dbPath;

    public LegacySchemaMigrationTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"codeindex_legacy_upgrade_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        _dbPath = Path.Combine(_dbDir, "codeindex.db");
        SeedLegacyDb(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dbDir))
            {
                // Restore writable perms in case a test left the dir read-only. / 読み取り専用状態を戻す。
                if (!OperatingSystem.IsWindows())
                {
                    try { File.SetUnixFileMode(_dbDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
                }
                Directory.Delete(_dbDir, recursive: true);
            }
        }
        catch { /* ignore */ }
    }

    private static void SeedLegacyDb(string dbPath)
    {
        // Build a DB that matches the pre-column layout: no start_line / end_line /
        // body_start_line / body_end_line / signature / visibility / return_type /
        // container_kind / container_name / container_qualified_name / family_key on symbols,
        // no symbol_references, no file_issues,
        // no checksum / indexed_at on files. This mirrors what older cdidx binaries produced.
        // 旧 cdidx が生成していたスキーマ。追加カラム・テーブルをすべて欠落させる。
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        Exec(conn, @"CREATE TABLE files (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        path        TEXT NOT NULL UNIQUE,
                        lang        TEXT,
                        size        INTEGER,
                        lines       INTEGER,
                        modified    DATETIME
                    )");
        Exec(conn, @"CREATE TABLE chunks (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        chunk_index INTEGER NOT NULL,
                        start_line  INTEGER,
                        end_line    INTEGER,
                        content     TEXT,
                        UNIQUE(file_id, chunk_index)
                    )");
        Exec(conn, @"CREATE TABLE symbols (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        kind        TEXT,
                        name        TEXT,
                        line        INTEGER
                    )");

        // Seed one file and a handful of symbols on the legacy schema.
        // レガシースキーマに対してファイル1つとシンボルを数件投入。
        Exec(conn, "INSERT INTO files (id, path, lang, size, lines, modified) VALUES (1, 'src/Legacy.cs', 'csharp', 200, 20, '2025-06-01 00:00:00')");
        Exec(conn, @"INSERT INTO chunks (file_id, chunk_index, start_line, end_line, content) VALUES
                     (1, 0, 1, 20, 'class Legacy' || char(10) ||
                                   '{' || char(10) ||
                                   '    // top' || char(10) ||
                                   '' || char(10) ||
                                   '    void Alpha() {}' || char(10) ||
                                   '' || char(10) ||
                                   '' || char(10) ||
                                   '' || char(10) ||
                                   '    void Beta() {}' || char(10) ||
                                   '}')");
        Exec(conn, "INSERT INTO symbols (file_id, kind, name, line) VALUES (1, 'class', 'Legacy', 1)");
        Exec(conn, "INSERT INTO symbols (file_id, kind, name, line) VALUES (1, 'function', 'Alpha', 5)");
        Exec(conn, "INSERT INTO symbols (file_id, kind, name, line) VALUES (1, 'function', 'Beta', 9)");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void SeedGraphDbWithoutExactFallbackIndexes(string dbPath)
    {
        using var db = new DbContext(dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);

        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/session.py",
            Lang = "python",
            Size = 48,
            Lines = 2,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks([
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 2,
                Content = "def login(user, password):\n    return Run(user)\n",
            }
        ]);
        writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "login",
                Line = 1,
                StartLine = 1,
                EndLine = 2,
                Signature = "def login(user, password):",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Run",
                Line = 2,
                StartLine = 2,
                EndLine = 2,
                Signature = "Run(user)",
            }
        ]);
        writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Run",
                ReferenceKind = "call",
                Line = 2,
                Column = 12,
                Context = "return Run(user)",
                ContainerKind = "function",
                ContainerName = "login",
            }
        ]);
        writer.MarkGraphReady();

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            DROP INDEX IF EXISTS idx_symbol_refs_name_nocase;
            DROP INDEX IF EXISTS idx_symbol_refs_container_nocase;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static void DropSymbolExactFallbackIndex(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            DROP INDEX IF EXISTS idx_symbols_name_nocase;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void TryMigrateForRead_LegacyDb_ReadPathsDoNotCrash()
    {
        // Open through the normal read path — TryMigrateForRead adds the missing columns as NULL.
        // 通常の read path で開くと TryMigrateForRead が欠損カラムを NULL で追加する。
        using var db = new DbContext(_dbPath);
        db.TryMigrateForRead();

        // Confirm the migration actually ran: the new columns should now exist and be NULL.
        // マイグレーションが実際に走ったことを確認。
        using (var check = db.Connection.CreateCommand())
        {
            check.CommandText = "SELECT start_line, end_line, body_start_line, body_end_line, signature, visibility, return_type, container_kind, container_name, container_qualified_name, family_key FROM symbols WHERE name = 'Alpha'";
            using var r = check.ExecuteReader();
            Assert.True(r.Read());
            for (int i = 0; i < 11; i++) Assert.True(r.IsDBNull(i));
        }

        var reader = new DbReader(db.Connection);

        // GetOutline — falls back to line for NULL start/end. / start/end が NULL のとき line にフォールバック。
        var outline = reader.GetOutline("src/Legacy.cs");
        Assert.NotNull(outline);
        Assert.Equal(3, outline!.Symbols.Count);
        var alphaOutline = outline.Symbols.Single(s => s.Name == "Alpha");
        Assert.Equal(5, alphaOutline.Line);
        Assert.Equal(5, alphaOutline.StartLine);
        Assert.Equal(5, alphaOutline.EndLine);

        // SearchSymbols — by-name lookup must not crash on NULL ordinals.
        // by-name 検索が NULL 列でクラッシュしないこと。
        var hits = reader.SearchSymbols("Alpha");
        var alpha = Assert.Single(hits, s => s.Name == "Alpha");
        Assert.Equal("src/Legacy.cs", alpha.Path);
        Assert.Equal(5, alpha.StartLine);
        Assert.Equal(5, alpha.EndLine);

        // GetNearbySymbols — neighbor lookup around a focus line with NULL start_line.
        // 周辺シンボル取得。
        var nearby = reader.GetNearbySymbols("src/Legacy.cs", focusLine: 5, limit: 10);
        Assert.Contains(nearby, s => s.Name == "Beta");

        // GetUnusedSymbols — must NOT claim Alpha is unused just because TryMigrateForRead
        // created symbol_references as an empty table. A legacy DB upgraded on writable
        // storage has the tables but no graph content yet, so the only safe signal is
        // degraded (empty + GraphTableAvailable=false) until a real index run populates it.
        // migration で空の symbol_references を作っただけの DB で Alpha を「未使用」と
        // 報告してはいけない。populate されるまでは縮退扱いで空を返すのが正しい。
        var unused = reader.GetUnusedSymbols(limit: 10, kind: null, lang: null,
            pathPatterns: null, excludePathPatterns: null, excludeTests: false);
        Assert.Empty(unused);

        // AnalyzeSymbol — bundled inspect path exercises definitions + nearby + references.
        // inspect バンドル経路。
        var bundle = reader.AnalyzeSymbol("Alpha");
        Assert.NotNull(bundle);
        Assert.NotEmpty(bundle.Definitions);
        Assert.Equal("Alpha", bundle.Definitions[0].Name);
        Assert.Equal(5, bundle.Definitions[0].StartLine);
        // Same trust-boundary rule as read-only: migrated-but-unpopulated graph/issue tables
        // must be flagged degraded so callers cannot silently trust the empties.
        // 空テーブルは必ず縮退シグナルで呼び出し側に伝える。
        Assert.False(bundle.GraphTableAvailable);
        Assert.Empty(bundle.References);
        Assert.Empty(bundle.Callers);
        Assert.Empty(bundle.Callees);
        var status = reader.GetStatus();
        Assert.False(status.GraphTableAvailable);
        Assert.False(status.IssuesTableAvailable);
    }

    [Fact]
    public void TryMigrateForRead_ReadOnlyLegacyDb_MigrationSkippedAndReadPathsSurvive()
    {
        // Read-only half of the contract: when the connection cannot write, TryMigrateForRead
        // must silently no-op (SQLITE_READONLY / code 8), the legacy columns must remain absent,
        // and the reader fallback expressions must still drive the query paths without crashing.
        // read-only 契約: 書き込みできない接続では TryMigrateForRead が黙って no-op になり、
        // レガシーカラムは追加されないまま、fallback 式で read path が動き続ける必要がある。
        using var db = new DbContext(_dbPath);

        // Simulate a read-only connection at the SQL layer so ALTER TABLE fails with SQLITE_READONLY.
        // query_only は SQL レイヤで書き込みを禁止し、ALTER TABLE が SQLITE_READONLY で失敗する。
        using (var pragma = db.Connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = ON";
            pragma.ExecuteNonQuery();
        }

        // Should not throw — the SQLITE_READONLY catch inside TryMigrateForRead swallows it.
        // SQLITE_READONLY を握り潰す catch があるため例外は出ない。
        db.TryMigrateForRead();

        // The added columns must still be missing — migration really was skipped.
        // マイグレーションが本当にスキップされ、追加カラムが未だ存在しないことを確認。
        using (var check = db.Connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(symbols)";
            using var r = check.ExecuteReader();
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) cols.Add(r.GetString(1));
            Assert.DoesNotContain("start_line", cols);
            Assert.DoesNotContain("end_line", cols);
            Assert.DoesNotContain("signature", cols);
            Assert.DoesNotContain("visibility", cols);
        }

        // DbReader must detect the legacy layout at construction and pick the fallback SQL.
        // DbReader は構築時にレガシーレイアウトを検出し、fallback SQL に切り替える必要がある。
        var reader = new DbReader(db.Connection);

        var outline = reader.GetOutline("src/Legacy.cs");
        Assert.NotNull(outline);
        var alphaOutline = outline!.Symbols.Single(s => s.Name == "Alpha");
        Assert.Equal(5, alphaOutline.StartLine);
        Assert.Equal(5, alphaOutline.EndLine);

        var hits = reader.SearchSymbols("Alpha");
        Assert.Contains(hits, s => s.Name == "Alpha" && s.StartLine == 5);

        var nearby = reader.GetNearbySymbols("src/Legacy.cs", focusLine: 5, limit: 10);
        Assert.Contains(nearby, s => s.Name == "Beta");

        // #58 crash site — must not throw even when both the NEW columns AND the
        // symbol_references table are absent (migration skipped on read-only). Without a
        // references table every symbol would look unused, so the correct degradation is an
        // empty list rather than a flood of false positives or a crash.
        // 追加カラムと symbol_references テーブルの両方が欠けていてもクラッシュしないこと。
        // 参照が無い環境では全シンボルが未使用に見えるため、空リストへ縮退するのが正しい。
        var unused = reader.GetUnusedSymbols(limit: 10, kind: null, lang: null,
            pathPatterns: null, excludePathPatterns: null, excludeTests: false);
        Assert.Empty(unused);

        // AnalyzeSymbol still resolves definitions/nearby from the symbols table, but
        // references/callers/callees must degrade to empty lists instead of crashing.
        // definitions と nearby は symbols テーブルから解決でき、references 系は空に縮退する。
        var bundle = reader.AnalyzeSymbol("Alpha");
        Assert.NotEmpty(bundle.Definitions);
        Assert.Equal("Alpha", bundle.Definitions[0].Name);
        Assert.Equal(5, bundle.Definitions[0].StartLine);
        Assert.Empty(bundle.References);
        Assert.Empty(bundle.Callers);
        Assert.Empty(bundle.Callees);
        // Degraded-state signal: callers must be able to tell "table missing" from "real 0 hits".
        // テーブル欠損と真の 0 件を区別するシグナル。
        Assert.False(bundle.GraphTableAvailable);

        // Public commands backed by missing tables must also degrade, not crash.
        // 欠損テーブルに依存する公開コマンドも、クラッシュせず空を返すこと。
        Assert.Empty(reader.GetFileDependencies());
        Assert.Empty(reader.GetIssues());

        // Status must surface the degraded state so AI clients cannot silently misread a
        // missing-table 0-count as a real "clean" result.
        // 欠損テーブルによる 0 を「本当に 0」と誤解しないように status が必ずシグナルを出す。
        var status = reader.GetStatus();
        Assert.False(status.GraphTableAvailable);
        Assert.False(status.IssuesTableAvailable);
    }

    [Fact]
    public void TryMigrateForRead_PartialDdlFailure_RecordsStepAndEmitsActionableWarning()
    {
        // Issue #1516: when a DDL step fails part-way through (restricted mount: WORM /
        // sandbox / network share), the previous broad swallow left no trace. The next read
        // would hit a cryptic `no such column` instead of an actionable "migration partial,
        // re-run on writable storage" message. The new contract: capture the failing step
        // and SQLite error code on the DbContext, and emit a single stderr warning that
        // points at writable-storage recovery so the user has a concrete next action.
        // #1516: 部分 DDL 失敗時にステップ名と SQLite error code を記録し、stderr に
        // writable storage への誘導を 1 行で出すことで、後続の cryptic な「no such column」を
        // 行動可能な診断に置き換える契約。
        using var db = new DbContext(_dbPath);

        // query_only = ON makes any DDL fail with SQLITE_READONLY (code 8) — the same surface
        // as a real restricted-mount partial-migration failure. The first step in the read
        // migration is `CREATE TABLE reference_lines`, which has no pre-existing table to
        // satisfy `IF NOT EXISTS`, so it is the failing step here.
        // query_only=ON で readonly-class 失敗を再現する。
        using (var pragma = db.Connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = ON";
            pragma.ExecuteNonQuery();
        }

        var originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        try
        {
            db.TryMigrateForRead();
        }
        finally
        {
            Console.SetError(originalError);
        }

        // Structured failure: step description, SQLite error code, and an actionable hint
        // are all available to callers (CLI, MCP, programmatic) without reparsing stderr.
        // 構造化された失敗情報を呼び出し側へ提供する。
        Assert.NotNull(db.LastMigrationFailure);
        Assert.False(string.IsNullOrWhiteSpace(db.LastMigrationFailure!.Step));
        Assert.Equal("CREATE TABLE reference_lines", db.LastMigrationFailure.Step);
        Assert.Equal(8, db.LastMigrationFailure.SqliteErrorCode);
        Assert.Contains("writable storage", db.LastMigrationFailure.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chmod", db.LastMigrationFailure.SuggestedAction, StringComparison.OrdinalIgnoreCase);

        // Single stderr line so the diagnostic is hard to miss but does not flood logs.
        // 1 行の stderr 警告。
        var stderr = capturedError.ToString();
        Assert.Contains("schema migration step", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE reference_lines", stderr, StringComparison.Ordinal);
        Assert.Contains("SQLite error 8", stderr, StringComparison.Ordinal);
        Assert.Contains("no such column", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("writable storage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryMigrateForRead_SuccessfulMigration_LeavesLastMigrationFailureNull()
    {
        // The diagnostic must stay opt-in: a normal migration on writable storage records
        // nothing, so callers can use `LastMigrationFailure is not null` as a clean signal.
        // 正常完了時は LastMigrationFailure が null のまま — シグナルとして利用できる契約。
        using var db = new DbContext(_dbPath);

        var originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        try
        {
            db.TryMigrateForRead();
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Null(db.LastMigrationFailure);
        Assert.DoesNotContain("schema migration step", capturedError.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryMigrateForRead_PartialDdlFailure_FailureSurvivesForLaterInspection()
    {
        // The recorded failure must outlive the TryMigrateForRead call so a caller that hits
        // a subsequent `no such column` SqliteException can read LastMigrationFailure to
        // explain *why* the column is missing — i.e. the migration partially failed and the
        // user should re-run on writable storage. This is the link the issue calls out:
        // "Cryptic `no such column` errors instead of a single clear ... message".
        // 失敗情報は呼び出し後も残り、後続の no-such-column エラーと突き合わせて原因を説明できる必要がある。
        using var db = new DbContext(_dbPath);
        using (var pragma = db.Connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = ON";
            pragma.ExecuteNonQuery();
        }

        var originalError = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            db.TryMigrateForRead();
        }
        finally
        {
            Console.SetError(originalError);
        }

        // Failure outlives the call — caller can correlate downstream errors against it.
        // TryMigrateForRead を抜けたあとも参照可能。
        var failure = db.LastMigrationFailure;
        Assert.NotNull(failure);
        Assert.Equal(8, failure!.SqliteErrorCode);

        // The migration aborted before adding `symbols.start_line`, so a query touching that
        // column still fails with SQLITE_ERROR (code 1) `no such column`. The user-facing
        // promise is that they no longer have to guess: LastMigrationFailure explains it.
        // start_line 列を参照する read が "no such column" で失敗することを確認 — その文脈で
        // LastMigrationFailure を見せれば原因が一目で分かる。
        using var failingRead = db.Connection.CreateCommand();
        failingRead.CommandText = "SELECT start_line FROM symbols LIMIT 1";
        var ex = Assert.Throws<SqliteException>(() => failingRead.ExecuteReader());
        Assert.Contains("no such column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DbContext_ReadOnlyFilesystem_FallsBackToReadOnlyOpen()
    {
        // Verify the DbContext read-only fallback for real — when the containing directory is
        // read-only, SQLite cannot create -journal / -shm / -wal side files, so a default
        // (writable) open fails with SQLITE_CANTOPEN. DbContext must retry with Mode=ReadOnly
        // and the subsequent read paths must work end-to-end on the legacy schema.
        // ディレクトリが read-only だと SQLite は副ファイルを作れず writable open が失敗するため、
        // DbContext は Mode=ReadOnly へフォールバックしなければならない。
        if (OperatingSystem.IsWindows())
        {
            // Windows directory permissions don't block SQLite journal creation the same way.
            // Windows のディレクトリパーミッションは journal 生成ブロックの意味が異なるためスキップ。
            return;
        }

        var originalMode = File.GetUnixFileMode(_dbDir);
        SqliteConnection.ClearAllPools();
        try
        {
            // Remove write and execute bits so -journal cannot be created. r-x only.
            // 書き込み・グループ・other を落として、副ファイル生成を禁止する。
            File.SetUnixFileMode(_dbDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            using var db = new DbContext(_dbPath);
            Assert.True(db.IsReadOnly, "DbContext should have fallen back to read-only open.");

            // Migration is skipped (catches SQLITE_READONLY). Reader still builds and query
            // paths — including the previously unguarded deps / issues — degrade to empty.
            // マイグレーションはスキップされ、deps / issues も空で縮退する。
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection);
            Assert.Empty(reader.GetFileDependencies());
            Assert.Empty(reader.GetIssues());

            var outline = reader.GetOutline("src/Legacy.cs");
            Assert.NotNull(outline);
            Assert.Equal(3, outline!.Symbols.Count);
        }
        finally
        {
            File.SetUnixFileMode(_dbDir, originalMode);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void DbContext_OpenSqliteConnectionWithRetry_RetriesTransientBusy()
    {
        // The retry helper should back off on transient busy/locked failures before giving up.
        // retry helper は transient busy/locked ではすぐ諦めず backoff して再試行するべき。
        var attempts = 0;
        var sleeps = new List<int>();
        var connection = DbContext.OpenSqliteConnectionWithRetry(
            () => new SqliteConnection("Data Source=:memory:"),
            _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw CreateTransientBusyException();
            },
            sleep: sleeps.Add,
            maxOpenAttempts: 5);

        try
        {
            Assert.Equal(3, attempts);
            Assert.Equal(new[] { 50, 100 }, sleeps);
            Assert.NotNull(connection);
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public void DbContext_SynchronousPragma_AllowsSqliteSafetyLevelTransactionError()
    {
        var calls = 0;

        DbContext.ExecuteSynchronousPragmaWithFallback(sql =>
        {
            calls++;
            Assert.Equal($"PRAGMA synchronous={DbContext.DefaultSynchronousMode}", sql);
            throw CreateSqliteException("Safety level may not be changed inside a transaction", 1);
        });

        Assert.Equal(1, calls);
    }

    [Fact]
    public void DbContext_SynchronousPragma_RethrowsOtherSqliteErrors()
    {
        var ex = Assert.Throws<SqliteException>(() =>
            DbContext.ExecuteSynchronousPragmaWithFallback(_ =>
                throw CreateSqliteException("no such table: missing", 1)));

        Assert.Contains("no such table", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateExistingCodeIndexDb_RetriesTransientBusyOpen()
    {
        // backfill-fold validation must use the same retry/backoff path as the main open.
        // backfill-fold の validation も main open と同じ retry/backoff を使う必要がある。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_validation_retry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "codeindex.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();

            var attempts = 0;
            var sleeps = new List<int>();
            var valid = DbContext.TryValidateExistingCodeIndexDb(
                dbPath,
                openTarget =>
                {
                    var builder = new SqliteConnectionStringBuilder
                    {
                        DataSource = openTarget,
                        Mode = SqliteOpenMode.ReadWrite,
                    };
                    return new SqliteConnection(builder.ConnectionString);
                },
                connection =>
                {
                    attempts++;
                    if (attempts < 3)
                        throw CreateTransientBusyException();
                    connection.Open();
                },
                sleeps.Add,
                out var message,
                out var isNotFound);

            Assert.True(valid);
            Assert.Equal(3, attempts);
            Assert.Equal(new[] { 50, 100 }, sleeps);
            Assert.False(isNotFound);
            Assert.Equal(string.Empty, message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryValidateExistingCodeIndexDb_PublicWrapper_SucceedsForExistingDb()
    {
        // The public validation path should go through the same open logic and accept a real DB.
        // 公開 validation 経路も同じ open ロジックを通り、実在する DB を受け入れること。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_validation_public_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "codeindex.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();

            var valid = DbContext.TryValidateExistingCodeIndexDb(dbPath, out var message, out var isNotFound);

            Assert.True(valid);
            Assert.False(isNotFound);
            Assert.Equal(string.Empty, message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static SqliteException CreateTransientBusyException()
        => CreateSqliteException("busy", 5);

    private static SqliteException CreateSqliteException(string message, int errorCode)
    {
        var exception = Activator.CreateInstance(
            typeof(SqliteException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [message, errorCode],
            culture: null) as SqliteException;

        return exception ?? throw new InvalidOperationException("Failed to create SqliteException for retry test.");
    }

    [Fact]
    public void InterruptedIndex_SchemaInitializedButNotCompleted_StaysDegraded()
    {
        // An index run that creates the schema (InitializeSchema) but is killed before
        // MarkIndexComplete runs must NOT be trusted as authoritative. PRAGMA user_version
        // should remain 0 until the very end of a successful index, so downstream reads see
        // the empty graph/issues tables as degraded rather than "clean".
        // InitializeSchema だけ走って MarkIndexComplete に到達しなかった index は、
        // user_version=0 のまま残り、readers は空テーブルを縮退扱いにしなければならない。
        var interruptedDir = Path.Combine(Path.GetTempPath(), $"codeindex_interrupted_{Guid.NewGuid():N}");
        Directory.CreateDirectory(interruptedDir);
        var interruptedDb = Path.Combine(interruptedDir, "codeindex.db");
        try
        {
            using (var db = new DbContext(interruptedDb))
            {
                db.InitializeSchema();
                // simulate interruption: no writes, no MarkIndexComplete.
                // 中断シミュレーション: 書き込みも MarkIndexComplete も走らない。
                Assert.Equal(0, db.GetUserVersion());
            }

            SqliteConnection.ClearAllPools();

            using (var db = new DbContext(interruptedDb))
            {
                var reader = new DbReader(db.Connection);
                // Empty graph/issues tables on an unstamped DB must be reported degraded.
                // 版印なしの DB で空テーブルは縮退として扱うこと。
                var status = reader.GetStatus();
                Assert.False(status.GraphTableAvailable);
                Assert.False(status.IssuesTableAvailable);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(interruptedDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PartialIndex_GraphReadyStampedButNotIssuesReady_IsTreatedAsPartialTrust()
    {
        // MCP indexing runs no validate pass, so only MarkGraphReady() fires. The reader
        // must trust graph data but still flag issues as degraded. This locks in the split
        // so MCP-built DBs cannot silently pass `validate` with a false clean signal.
        // MCP は graph のみ stamp するため、issues は縮退のまま残さねばならない。
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_partial_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                db.ClearReadyFlags();
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady(); // MCP-style: only graph, no issues
                var reader = new DbReader(db.Connection);
                var status = reader.GetStatus();
                Assert.True(status.GraphTableAvailable);
                Assert.False(status.IssuesTableAvailable);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InterruptedRefresh_OnAlreadyStampedDb_DemotesTrustUntilCompletion()
    {
        // Refresh safety: an already-stamped DB that begins a re-index must be demoted to
        // degraded at the start (ClearReadyFlags), so an interrupted refresh does not leave
        // mixed/partial state looking authoritative.
        // 既に stamp 済みの DB でも reindex 開始時に ClearReadyFlags() で degraded に戻すこと。
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_refresh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
                writer.MarkFoldReady();
                Assert.Equal(DbContext.CurrentSchemaVersion, db.GetUserVersion());

                // Simulate the start of a refresh: clear readiness. An interrupted refresh
                // would leave it at 0 — trust is correctly demoted.
                // refresh 開始を模擬。中断されればここで止まり、縮退のまま残る。
                db.ClearReadyFlags();
                Assert.Equal(0, db.GetUserVersion());

                var reader = new DbReader(db.Connection);
                var status = reader.GetStatus();
                Assert.False(status.GraphTableAvailable);
                Assert.False(status.IssuesTableAvailable);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DbContext_UriConnectionString_OpensReadOnlyWithImmutableFlag()
    {
        // CLI escape hatch: --db file:///abs/path?immutable=1 must be passed through to
        // SQLite so users on restricted sandboxes can bypass the normal open path. The
        // constructor should detect the URI prefix, skip the writable-open attempt, and
        // mark the connection read-only.
        // --db に URI を渡された場合は writable open をスキップし read-only で開くこと。
        var fileUri = new Uri(_dbPath).AbsoluteUri + "?immutable=1";
        using var db = new DbContext(fileUri);
        Assert.True(db.IsReadOnly);

        var reader = new DbReader(db.Connection);
        var outline = reader.GetOutline("src/Legacy.cs");
        Assert.NotNull(outline);
        Assert.Equal(3, outline!.Symbols.Count);
    }

    [Fact]
    public void ReadOnlyDb_MissingExactGraphFallbackIndexes_SurfacesDegradedSignal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_exact_signal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            SeedGraphDbWithoutExactFallbackIndexes(dbPath);

            var fileUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            using var db = new DbContext(fileUri);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);

            var referencesSignal = reader.GetReferencesExactQuerySignal();
            var callersSignal = reader.GetCallersExactQuerySignal();
            var calleesSignal = reader.GetCalleesExactQuerySignal();
            var bundle = reader.AnalyzeSymbol("Run", exact: true);

            Assert.False(referencesSignal.ExactIndexAvailable);
            Assert.Contains("idx_symbol_refs_name_nocase", referencesSignal.DegradedReason);
            Assert.False(callersSignal.ExactIndexAvailable);
            Assert.Contains("idx_symbol_refs_name_nocase", callersSignal.DegradedReason);
            Assert.False(calleesSignal.ExactIndexAvailable);
            Assert.Contains("idx_symbol_refs_container_nocase", calleesSignal.DegradedReason);
            Assert.False(bundle.ExactIndexAvailable ?? true);
            Assert.Contains("idx_symbol_refs_name_nocase", bundle.DegradedReason);
            Assert.Contains("idx_symbol_refs_container_nocase", bundle.DegradedReason);
            Assert.Single(bundle.References);
            Assert.Single(bundle.Callers);
            Assert.Empty(bundle.Callees);

            var nonExactBundle = reader.AnalyzeSymbol("Run", exact: false);
            Assert.Null(nonExactBundle.ExactIndexAvailable);
            Assert.Null(nonExactBundle.DegradedReason);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReadOnlyDb_MissingExactSymbolFallbackIndex_SurfacesDegradedSignal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_symbol_exact_signal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            SeedGraphDbWithoutExactFallbackIndexes(dbPath);
            DropSymbolExactFallbackIndex(dbPath);

            var fileUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            using var db = new DbContext(fileUri);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);

            var symbolsSignal = reader.GetSymbolsExactQuerySignal();
            var definitionSignal = reader.GetDefinitionExactQuerySignal();
            var symbolResults = reader.SearchSymbols("Run", exact: true);
            var definitionResults = reader.GetDefinitions("Run", exact: true);

            Assert.False(symbolsSignal.ExactIndexAvailable);
            Assert.Contains("idx_symbols_name_nocase", symbolsSignal.DegradedReason);
            Assert.False(definitionSignal.ExactIndexAvailable);
            Assert.Contains("idx_symbols_name_nocase", definitionSignal.DegradedReason);
            Assert.Single(symbolResults);
            Assert.Single(definitionResults);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WritableLegacyDb_MissingExactSymbolFallbackIndex_SelfHealsDuringReadMigration()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_symbol_exact_writable_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            SeedGraphDbWithoutExactFallbackIndexes(dbPath);
            DropSymbolExactFallbackIndex(dbPath);

            using var db = new DbContext(dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);

            var symbolsSignal = reader.GetSymbolsExactQuerySignal();
            var definitionSignal = reader.GetDefinitionExactQuerySignal();

            Assert.True(symbolsSignal.ExactIndexAvailable);
            Assert.Null(symbolsSignal.DegradedReason);
            Assert.True(definitionSignal.ExactIndexAvailable);
            Assert.Null(definitionSignal.DegradedReason);

            using var check = db.Connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name='idx_symbols_name_nocase'";
            Assert.NotNull(check.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReadOnlyDb_NonExactAnalyzeSymbol_DoesNotDependOnHiddenExactAnchor()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_symbol_nonexact_hidden_exact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            using (var seedDb = new DbContext(dbPath))
            {
                seedDb.InitializeSchema();
                var writer = new DbWriter(seedDb.Connection);

                var aRunFileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/arun.py",
                    Lang = "python",
                    Size = 40,
                    Lines = 1,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = Guid.NewGuid().ToString("N"),
                });
                writer.InsertChunks([
                    new ChunkRecord
                    {
                        FileId = aRunFileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 1,
                        Content = "def ARun(): pass\n",
                    }
                ]);
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = aRunFileId,
                        Kind = "function",
                        Name = "ARun",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                        Signature = "def ARun():",
                    }
                ]);

                var runFileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/run.py",
                    Lang = "python",
                    Size = 38,
                    Lines = 1,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = Guid.NewGuid().ToString("N"),
                });
                writer.InsertChunks([
                    new ChunkRecord
                    {
                        FileId = runFileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 1,
                        Content = "def Run(): pass\n",
                    }
                ]);
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = runFileId,
                        Kind = "function",
                        Name = "Run",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                        Signature = "def Run():",
                    }
                ]);
                writer.MarkGraphReady();
            }

            DropSymbolExactFallbackIndex(dbPath);

            var fileUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            using var readOnlyDb = new DbContext(fileUri);
            readOnlyDb.TryMigrateForRead();
            var reader = new DbReader(readOnlyDb.Connection, readOnlyDb.IsReadOnly);

            var expected = Assert.Single(reader.SearchSymbols("run", limit: 1, exact: false));
            var bundle = reader.AnalyzeSymbol("run", limit: 1, exact: false);

            Assert.Null(bundle.ExactIndexAvailable);
            Assert.Null(bundle.DegradedReason);
            Assert.NotNull(bundle.File);
            Assert.Equal(expected.Path, bundle.File!.Path);
            var definition = Assert.Single(bundle.Definitions);
            Assert.Equal(expected.Name, definition.Name);
            Assert.Equal(expected.Path, definition.Path);
            Assert.Equal("Run", definition.Name);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UpdateMode_OnLegacyDb_MustNotStampReadiness()
    {
        // Update mode (--commits / --files) touches only a subset of files, so stamping
        // readiness on a previously-degraded DB would falsely promote every untouched file
        // to authoritative. This test stamps `canStampReadiness=false`-equivalent semantics
        // at the DbWriter boundary and verifies no ready bit flips on.
        // update モードが legacy DB を trusted に昇格させないことを確認。
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_update_legacy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            // Seed a legacy-shaped DB: InitializeSchema ran but readiness never stamped
            // (simulates a DB created by an older binary or a prior partial run).
            // 旧版バイナリ相当: スキーマだけ作って readiness は打たれていない状態。
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                Assert.Equal(0, db.GetUserVersion());
            }

            // Simulate RunIndex entry: capture wasFullyReady BEFORE clearing. Since the
            // starting user_version is 0, wasFullyReady is false, so canStampReadiness is
            // false in update mode — MarkReady must not fire.
            // RunIndex を模擬: wasFullyReady=false のため update モードでは stamp しない。
            using (var db = new DbContext(dbPath))
            {
                var wasFullyReady = db.GetUserVersion() == DbContext.CurrentSchemaVersion;
                Assert.False(wasFullyReady);
                db.ClearReadyFlags();
                // pretend RunUpdateMode ran with errors==0 but canStampReadiness=!isUpdate||wasFullyReady = false
                // so neither MarkGraphReady nor MarkIssuesReady fires.
                Assert.Equal(0, db.GetUserVersion());

                var reader = new DbReader(db.Connection);
                var status = reader.GetStatus();
                Assert.False(status.GraphTableAvailable);
                Assert.False(status.IssuesTableAvailable);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DbPathResolver_NormalizesFileUri_ForProjectRootResolution()
    {
        // The `--db file:///.cdidx/codeindex.db?immutable=1` escape hatch must not poison
        // metadata. ResolveProjectRootForQuery previously passed the raw URI into
        // Path.GetFullPath, producing /cwd/file:/abs/... garbage. Now it normalizes URIs
        // to local paths before the `.cdidx` heuristic, so workspace / git lookups recover
        // the same project root as a plain filesystem path.
        // --db が file: URI の場合でも、.cdidx 判定前にローカルパス化して正しい project root を返す。
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_uri_root_{Guid.NewGuid():N}");
        var cdidxDir = Path.Combine(dir, ".cdidx");
        Directory.CreateDirectory(cdidxDir);
        var dbPath = Path.Combine(cdidxDir, "codeindex.db");
        try
        {
            File.WriteAllBytes(dbPath, Array.Empty<byte>());
            var uri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var plainRoot = DbPathResolver.ResolveProjectRootForQuery(dbPath);
            var uriRoot = DbPathResolver.ResolveProjectRootForQuery(uri);
            Assert.NotNull(plainRoot);
            Assert.NotNull(uriRoot);
            Assert.Equal(plainRoot, uriRoot);
            Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(uriRoot!));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DbContext_BareFileUri_FallsBackToFilesystemPath()
    {
        // A bare `file://` URI without immutable=1 / mode=ro must NOT take the read-only
        // escape hatch — SQLite's default open mode is read-write-CREATE, which would turn
        // a `status` on a missing-path URI into silent DB creation. Instead, DbContext
        // normalizes the URI to a filesystem path and uses the normal writable-open path.
        // 明示的 read-only 要求なしの file: URI は writable-open ではなく通常経路にフォールバック。
        using var db = new DbContext(new Uri(_dbPath).AbsoluteUri);
        Assert.False(db.IsReadOnly);
        var reader = new DbReader(db.Connection);
        Assert.NotNull(reader.GetOutline("src/Legacy.cs"));
    }

    [Fact]
    public void Rebuild_ClearsReadyFlagsBeforeDroppingTables()
    {
        // Crash-window guard: a previously-stamped DB that begins a --rebuild must clear the
        // readiness bits BEFORE DropAll / InitializeSchema, so a crash between recreating
        // empty tables and end-of-run stamping cannot leave old bits blessing empty data.
        // This mirrors the ordering in IndexCommandRunner / McpToolHandlers.
        // rebuild 開始時は DropAll より前に readiness をクリアする順序の固定。
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_rebuild_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            // Stamp the DB as a previously-successful index.
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
                writer.MarkFoldReady();
                Assert.Equal(DbContext.CurrentSchemaVersion, db.GetUserVersion());
            }
            SqliteConnection.ClearAllPools();

            // Simulate the production rebuild sequence: open → ClearReadyFlags → DropAll →
            // InitializeSchema. Interrupt before any writes or MarkReady. Bits must be 0.
            // 本番の rebuild 順序を模擬し、stamp 前に中断。readiness は 0 でなければならない。
            using (var db = new DbContext(dbPath))
            {
                db.ClearReadyFlags();
                db.DropAll();
                db.InitializeSchema();
                // intentionally no writes, no MarkGraphReady / MarkIssuesReady
                Assert.Equal(0, db.GetUserVersion());

                var reader = new DbReader(db.Connection);
                var status = reader.GetStatus();
                Assert.False(status.GraphTableAvailable);
                Assert.False(status.IssuesTableAvailable);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CompletedIndex_MarkIndexCompleteStampsUserVersion_AndTablesAreTrusted()
    {
        // Companion to the interrupted-index test: a full successful cycle
        // (InitializeSchema → writes → MarkIndexComplete) stamps user_version and the tables
        // are then trusted even if empty (legitimate docs-only repo).
        // 成功 index では MarkIndexComplete が版印を打ち、空テーブルでも trusted になる。
        var completedDir = Path.Combine(Path.GetTempPath(), $"codeindex_completed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(completedDir);
        var completedDb = Path.Combine(completedDir, "codeindex.db");
        try
        {
            using (var db = new DbContext(completedDb))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
                writer.MarkFoldReady();
                Assert.Equal(DbContext.CurrentSchemaVersion, db.GetUserVersion());

                var reader = new DbReader(db.Connection);
                var status = reader.GetStatus();
                Assert.True(status.GraphTableAvailable);
                Assert.True(status.IssuesTableAvailable);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(completedDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReadOnlyLegacyDb_MissingSignatureColumn_DepsAndImpactDoNotCrashOnCSharp()
    {
        // Issue #431 regression: `BuildMetadataTargetKindExpr` (the shared C#/JS-TS/other
        // helper used by deps `target_files` / `target_ambiguity` and impact
        // `IsMetadataTargetUnambiguous`) intentionally degrades to a name-heuristic
        // fallback (`name LIKE '%Attribute'`) when `_symbolColumns` does not contain
        // `signature`. That guard protects legacy / read-only DBs where `TryMigrateForRead`
        // could not add the column in place — without it, the C# clause would reference
        // `s.signature` and crash every `deps` / `impact` query with
        // `SqliteException: no such column: s.signature`.
        //
        // The happy-path (column present) is pinned by
        // `GetFileDependencies_CSharp_IndirectAttributeInheritance_ResolvesAsMetadataTarget`
        // in DbReaderTests, and `TryMigrateForRead_LegacyDb_ReadPathsDoNotCrash` covers the
        // writable-migration path where `TryMigrateForRead` adds `signature` back as NULL.
        // This test closes the remaining gap: a DB where the column never becomes visible
        // to `_symbolColumns` at all (mirrors the real-world read-only-filesystem shape from
        // issue #431) must still return from `deps` / `impact` without crashing.
        //
        // Issue #431 回帰テスト: legacy / read-only DB で `TryMigrateForRead` が signature
        // 列を追加できない場合、`BuildMetadataTargetKindExpr` が命名規約 fallback
        // (`name LIKE '%Attribute'`) に縮退する分岐の回帰カバー。
        // カラム復活できる通常経路は DbReaderTests の
        // `GetFileDependencies_CSharp_IndirectAttributeInheritance_ResolvesAsMetadataTarget`
        // と本ファイルの `TryMigrateForRead_LegacyDb_ReadPathsDoNotCrash` が押さえており、
        // 本テストは `_symbolColumns` に signature が入らないまま deps / impact を呼んだ時に
        // `no such column: s.signature` で落ちないことを確認する。
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_issue431_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            // Seed a fully modern schema with a C# attribute class + a user that references it
            // via `[Audit]`, then stamp GraphReady so `_hasReferencesTable` becomes true.
            // 現行スキーマで index した後に GraphReady を立て、C# の attribute 依存を 1 本仕込む。
            using (var seed = new DbContext(dbPath))
            {
                seed.InitializeSchema();
                var writer = new DbWriter(seed.Connection);

                var auditFileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/Audit.cs",
                    Lang = "csharp",
                    Size = 64,
                    Lines = 3,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = Guid.NewGuid().ToString("N"),
                });
                writer.InsertChunks([
                    new ChunkRecord
                    {
                        FileId = auditFileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 3,
                        Content = "public class AuditAttribute : Attribute\n{\n}\n",
                    }
                ]);
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = auditFileId,
                        Kind = "class",
                        Name = "AuditAttribute",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 3,
                        Signature = "public class AuditAttribute : Attribute",
                        Visibility = "public",
                    }
                ]);

                var userFileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/User.cs",
                    Lang = "csharp",
                    Size = 72,
                    Lines = 4,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = Guid.NewGuid().ToString("N"),
                });
                writer.InsertChunks([
                    new ChunkRecord
                    {
                        FileId = userFileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 4,
                        Content = "[Audit]\npublic class User\n{\n}\n",
                    }
                ]);
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = userFileId,
                        Kind = "class",
                        Name = "User",
                        Line = 2,
                        StartLine = 2,
                        EndLine = 4,
                        Signature = "public class User",
                        Visibility = "public",
                    }
                ]);
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = userFileId,
                        SymbolName = "Audit",
                        ReferenceKind = "attribute",
                        Line = 1,
                        Column = 2,
                        Context = "[Audit]",
                        ContainerKind = "class",
                        ContainerName = "User",
                    }
                ]);
                writer.MarkGraphReady();
            }

            SqliteConnection.ClearAllPools();

            // Drop the signature column so `_symbolColumns` will not contain it. This is the
            // faithful in-test equivalent of a legacy DB on read-only storage where
            // `TryMigrateForRead` could not run `EnsureColumn("symbols", "signature", ...)`.
            // signature 列を落として legacy / read-only 相当の状態に持っていく。
            using (var drop = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                drop.Open();
                Exec(drop, "ALTER TABLE symbols DROP COLUMN signature");
            }

            SqliteConnection.ClearAllPools();

            // Reopen as read-only (immutable=1). DbContext sets `_isReadOnly=true`, so
            // `TryMigrateForRead` returns early without re-adding `signature` — the reader
            // genuinely sees `_symbolColumns` without the column.
            // immutable=1 で read-only open。TryMigrateForRead は早期 return するため
            // signature 列は復活せず、reader 側の `_symbolColumns` から抜けたままになる。
            var fileUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            using var db = new DbContext(fileUri);
            Assert.True(db.IsReadOnly);
            db.TryMigrateForRead();

            using (var check = db.Connection.CreateCommand())
            {
                check.CommandText = "PRAGMA table_info(symbols)";
                using var r = check.ExecuteReader();
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (r.Read()) cols.Add(r.GetString(1));
                Assert.DoesNotContain("signature", cols);
            }

            var reader = new DbReader(db.Connection, db.IsReadOnly);

            // `deps`: exercises `BuildMetadataTargetKindExpr` twice (inside the `target_files`
            // and `target_ambiguity` CTEs). The C# attribute-naming fallback still routes the
            // `[Audit]` reference to `AuditAttribute` via the suffix alias, so the deps edge
            // from `User.cs` → `Audit.cs` must survive even without `signature`.
            // deps は target_files / target_ambiguity の両方で fallback 分岐を踏むが、
            // `[Audit]` の suffix alias が `AuditAttribute` に解決するため edge は生き残る。
            var deps = reader.GetFileDependencies(limit: 50);
            var edge = Assert.Single(deps, d => d.SourcePath == "src/User.cs" && d.TargetPath == "src/Audit.cs");
            Assert.Contains("AuditAttribute", edge.Symbols);

            // `impact`: callers reject metadata kinds, so this flows through
            // `GetFileDependencyHintsToResolvedType` which calls `IsMetadataTargetUnambiguous`
            // (also backed by `BuildMetadataTargetKindExpr`). With a single class-like target
            // the fallback mode surfaces `User.cs` as a heuristic file-level consumer.
            // callers は metadata を弾くため、heuristic fallback 経路で
            // `IsMetadataTargetUnambiguous` が fallback SQL を発行する。
            var impact = reader.AnalyzeImpact("AuditAttribute");
            Assert.NotNull(impact);
            Assert.Equal("file_dependency_hints", impact.ImpactMode);
            Assert.True(impact.Heuristic);
            Assert.Contains(impact.FileImpacts, f => f.SourcePath == "src/User.cs");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureColumn_RecoversFromRacedAddColumn_RegardlessOfErrorMessageWording()
    {
        // Issue #1532 regression: the previous race-recovery catch in EnsureColumn keyed on
        // `ex.Message.Contains("duplicate column name")` which is English-only and tied to
        // SQLite's current wording. On a localized SQLite build or a future wording change
        // the catch would miss, leaving the migration aborted with a half-applied schema
        // that re-runs could not heal. The new recovery re-checks PRAGMA table_info inside
        // the catch and only swallows if the column is verifiably present — independent of
        // the exception message. The recovery policy now lives in DbColumnEnsurer so
        // DbContext does not need a production test-only hook.
        // #1532 回帰: 旧 catch は英語固有のメッセージ文字列に依存していたためロケール差や
        // 文言変更で再帰経路を取りこぼし、半適用スキーマが残り再実行でも修復できなかった。
        // 新しい復旧経路は PRAGMA table_info 相当で列存在を再確認し、メッセージに依存しない。
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_ensure_column_race_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            SeedLegacyDb(dbPath);

            var alterAttempted = 0;
            DbColumnEnsurer.EnsureColumn(
                () => ColumnExists(dbPath, "symbols", "start_line"),
                () =>
                {
                    Interlocked.Increment(ref alterAttempted);
                    using var altConn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
                    altConn.Open();
                    using var alt = altConn.CreateCommand();
                    alt.CommandText = "ALTER TABLE symbols ADD COLUMN start_line INTEGER";
                    alt.ExecuteNonQuery();
                    throw CreateSyntheticSqliteError(1, "localized or future duplicate-column wording for #1532");
                });

            Assert.Equal(1, alterAttempted);
            Assert.True(ColumnExists(dbPath, "symbols", "start_line"));

            // DbContext still uses DbColumnEnsurer for normal migrations: a later migration
            // sees start_line already present and completes the remaining columns.
            // DbContext の通常 migration も同じ helper を使い、後続列を追加できることを確認する。
            using var db = new DbContext(dbPath);
            db.TryMigrateForRead();
            Assert.Null(db.LastMigrationFailure);
            using (var check = db.Connection.CreateCommand())
            {
                check.CommandText = "PRAGMA table_info(symbols)";
                using var r = check.ExecuteReader();
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (r.Read()) cols.Add(r.GetString(1));
                Assert.Contains("start_line", cols);
                Assert.Contains("end_line", cols);
                Assert.Contains("signature", cols);
                Assert.Contains("container_kind", cols);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureColumn_AlterFailureWithoutColumnPresent_PropagatesInsteadOfBeingSwallowed()
    {
        // Companion guarantee: the new catch is gated on `ColumnExists` returning true. If
        // ALTER fails for an unrelated reason and the column is genuinely still absent,
        // the SqliteException must propagate instead of silently producing a broken DB.
        // We inject a synthetic SqliteException through DbColumnEnsurer to simulate any
        // non-duplicate-column failure mode — including one with localized/future wording.
        // ALTER が duplicate-column 以外の理由で失敗し、かつカラムが実在しない場合は、
        // 新 catch（ColumnExists 判定）でも握り潰されず伝播する必要がある。
        var dir = Path.Combine(Path.GetTempPath(), $"codeindex_ensure_column_propagate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "codeindex.db");
        try
        {
            SeedLegacyDb(dbPath);

            var thrown = Assert.Throws<SqliteException>(() =>
                DbColumnEnsurer.EnsureColumn(
                    () => ColumnExists(dbPath, "symbols", "start_line"),
                    () => throw CreateSyntheticSqliteError(1, "synthetic non-duplicate failure for #1532 regression")));

            Assert.Contains("synthetic non-duplicate failure", thrown.Message, StringComparison.Ordinal);
            Assert.False(ColumnExists(dbPath, "symbols", "start_line"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static SqliteException CreateSyntheticSqliteError(int errorCode, string message)
    {
        var exception = Activator.CreateInstance(
            typeof(SqliteException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [message, errorCode],
            culture: null) as SqliteException;

        return exception ?? throw new InvalidOperationException("Failed to create synthetic SqliteException.");
    }

    private static bool ColumnExists(string dbPath, string tableName, string columnName)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
