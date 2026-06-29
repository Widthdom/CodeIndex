using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class DbSchemaConstraintTests
{
    [Theory]
    [InlineData("chunks", "INSERT INTO chunks (file_id, chunk_index, start_line, end_line, content) VALUES (NULL, 0, 1, 1, 'content')")]
    [InlineData("reference_lines", "INSERT INTO reference_lines (file_id, line, context) VALUES (NULL, 1, 'context')")]
    [InlineData("symbols", "INSERT INTO symbols (file_id, kind, name, line) VALUES (NULL, 'function', 'MissingFile', 1)")]
    [InlineData("symbol_references", "INSERT INTO symbol_references (file_id, symbol_name, reference_kind, line) VALUES (NULL, 'MissingFile', 'call', 1)")]
    [InlineData("file_issues", "INSERT INTO file_issues (file_id, kind, line, message) VALUES (NULL, 'parse_error', 1, 'message')")]
    public void InitializeSchema_RequiredFileForeignKeysRejectNull(string tableName, string insertSql)
    {
        Assert.False(string.IsNullOrWhiteSpace(tableName));

        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_schema_constraints_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(dbPath);
            db.InitializeSchema();

            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = insertSql;
            var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
            Assert.Contains("NOT NULL", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("file_id", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void InitializeSchema_LegacyNullableFileForeignKeys_AreCleanedAndConstrained()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_schema_migration_{Guid.NewGuid():N}.db");
        try
        {
            SeedLegacyNullableFileIdSchema(dbPath);

            using (var db = new DbContext(dbPath))
                db.InitializeSchema();

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
            conn.Open();
            foreach (var table in new[] { "chunks", "reference_lines", "symbols", "symbol_references", "file_issues" })
            {
                Assert.True(FileIdIsNotNull(conn, table), $"{table}.file_id should be NOT NULL after migration.");
                Assert.Equal(0L, CountRows(conn, table, "file_id IS NULL"));
                Assert.Equal(1L, CountRows(conn, table, "file_id = 1"));
            }
            Assert.Equal(0L, CountFtsMatches(conn, "orphan"));
            Assert.Equal(1L, CountFtsMatches(conn, "ok"));
            Assert.Contains("reference_lines", ForeignKeyTargets(conn, "symbol_references"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void InitializeSchema_KindCheckConstraintsMatchCatalog_Issue4178()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_schema_kind_checks_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
            conn.Open();
            var symbolsSql = ReadCreateSql(conn, "symbols");
            var referencesSql = ReadCreateSql(conn, "symbol_references");

            AssertSameSet(SymbolKindCatalog.SymbolKinds, ExtractCheckValues(symbolsSql, "kind"));
            AssertSameSet(SymbolKindCatalog.SymbolKinds, ExtractCheckValues(symbolsSql, "container_kind"));
            AssertSameSet(SymbolKindCatalog.ReferenceKinds, ExtractCheckValues(referencesSql, "reference_kind"));
            AssertSameSet(SymbolKindCatalog.SymbolKinds, ExtractCheckValues(referencesSql, "container_kind"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                TestProjectHelper.DeleteFile(dbPath);
        }
    }

    private static void SeedLegacyNullableFileIdSchema(string dbPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        conn.Open();
        Exec(conn, """
            CREATE TABLE files (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                path        TEXT NOT NULL UNIQUE,
                lang        TEXT,
                size        INTEGER,
                lines       INTEGER,
                checksum    TEXT,
                modified    DATETIME,
                generated   INTEGER NOT NULL DEFAULT 0,
                indexed_at  DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE chunks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER REFERENCES files(id) ON DELETE CASCADE,
                chunk_index INTEGER NOT NULL,
                start_line INTEGER,
                end_line INTEGER,
                content TEXT,
                UNIQUE(file_id, chunk_index)
            );
            CREATE TABLE reference_lines (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER REFERENCES files(id) ON DELETE CASCADE,
                line INTEGER NOT NULL,
                context TEXT NOT NULL,
                UNIQUE(file_id, line)
            );
            CREATE TABLE symbols (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER REFERENCES files(id) ON DELETE CASCADE,
                kind TEXT,
                name TEXT,
                line INTEGER
            );
            CREATE TABLE symbol_references (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER REFERENCES files(id) ON DELETE CASCADE,
                symbol_name TEXT,
                reference_kind TEXT,
                line INTEGER,
                column_number INTEGER,
                context TEXT,
                container_kind TEXT,
                container_name TEXT
            );
            CREATE TABLE file_issues (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER REFERENCES files(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                line INTEGER NOT NULL DEFAULT 0,
                message TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE fts_chunks USING fts5(
                content,
                content='chunks',
                content_rowid='id'
            );
            INSERT INTO files (id, path, lang, size, lines, checksum, modified) VALUES (1, 'src/Legacy.cs', 'csharp', 1, 1, 'abc', '2026-01-01 00:00:00');
            INSERT INTO chunks (file_id, chunk_index, start_line, end_line, content) VALUES (1, 0, 1, 1, 'ok'), (NULL, 1, 1, 1, 'orphan');
            INSERT INTO fts_chunks(rowid, content) SELECT id, content FROM chunks;
            INSERT INTO reference_lines (file_id, line, context) VALUES (1, 1, 'ok'), (NULL, 2, 'orphan');
            INSERT INTO symbols (file_id, kind, name, line) VALUES (1, 'function', 'Ok', 1), (NULL, 'function', 'Orphan', 2);
            INSERT INTO symbol_references (file_id, symbol_name, reference_kind, line) VALUES (1, 'Ok', 'call', 1), (NULL, 'Orphan', 'call', 2);
            INSERT INTO file_issues (file_id, kind, line, message) VALUES (1, 'parse_error', 1, 'ok'), (NULL, 'parse_error', 2, 'orphan');
            """);
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool FileIdIsNotNull(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "file_id", StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(3) != 0;
        }
        return false;
    }

    private static long CountRows(SqliteConnection conn, string tableName, string where)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE {where}";
        return (long)cmd.ExecuteScalar()!;
    }

    private static long CountFtsMatches(SqliteConnection conn, string query)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH @query";
        cmd.Parameters.AddWithValue("@query", query);
        return (long)cmd.ExecuteScalar()!;
    }

    private static string ReadCreateSql(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", tableName);
        return (string)cmd.ExecuteScalar()!;
    }

    private static string[] ExtractCheckValues(string createSql, string columnName)
    {
        var match = Regex.Match(
            createSql,
            $@"\b{Regex.Escape(columnName)}\s+TEXT\s+CHECK\s*\((?<constraint>.*?)\)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Expected CHECK constraint for {columnName} in: {createSql}");

        return Regex.Matches(match.Groups["constraint"].Value, "'(?<value>(?:''|[^'])*)'")
            .Cast<Match>()
            .Select(valueMatch => valueMatch.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertSameSet(IEnumerable<string> expected, string[] actual)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal).ToArray(),
            actual);
    }

    private static string[] ForeignKeyTargets(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA foreign_key_list({tableName})";
        using var reader = cmd.ExecuteReader();
        var targets = new List<string>();
        while (reader.Read())
            targets.Add(reader.GetString(2));
        return targets.ToArray();
    }
}
