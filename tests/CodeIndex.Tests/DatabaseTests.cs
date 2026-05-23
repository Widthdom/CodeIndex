using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for DbContext and DbWriter integration.
/// DbContextとDbWriterの統合テスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class DatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContext _db;
    private readonly DbWriter _writer;

    public DatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}.db");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
    }

    [Fact]
    public void InitializeSchema_CreatesAllTables()
    {
        // Verify tables exist by querying sqlite_master
        // sqlite_masterを問い合わせてテーブルの存在を確認
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        Assert.Contains("files", tables);
        Assert.Contains("chunks", tables);
        Assert.Contains("symbols", tables);
        Assert.Contains("symbol_references", tables);
        Assert.Contains("fts_chunks", tables);
    }

    [Fact]
    public void Dispose_AfterWriteWork_RunsOptimizePragma()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_optimize_write_test_{Guid.NewGuid():N}.db");
        var optimizeCount = 0;
        DbContext.OptimizePragmaExecutedForTesting = dataSource =>
        {
            if (dataSource.Contains(Path.GetFileName(dbPath), StringComparison.Ordinal))
                optimizeCount++;
        };
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }

            Assert.Equal(1, optimizeCount);
        }
        finally
        {
            DbContext.OptimizePragmaExecutedForTesting = null;
            DeleteDbFiles(dbPath);
        }
    }

    [Fact]
    public void Dispose_WithoutWriteWork_SkipsOptimizePragma()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_optimize_read_test_{Guid.NewGuid():N}.db");
        var optimizeCount = 0;
        DbContext.OptimizePragmaExecutedForTesting = dataSource =>
        {
            if (dataSource.Contains(Path.GetFileName(dbPath), StringComparison.Ordinal))
                optimizeCount++;
        };
        try
        {
            using (var db = new DbContext(dbPath))
            {
            }

            Assert.Equal(0, optimizeCount);
        }
        finally
        {
            DbContext.OptimizePragmaExecutedForTesting = null;
            DeleteDbFiles(dbPath);
        }
    }

    [Fact]
    public void InitializeSchema_CreatesReferenceCompositeIndexesForGraphLookups()
    {
        var indexes = ReadIndexNames(_db.Connection, "symbol_references");

        Assert.Contains("idx_symbol_refs_name_kind", indexes);
        Assert.Contains("idx_symbol_refs_name_file", indexes);
        Assert.Contains("idx_symbol_refs_name_nocase_kind", indexes);
        Assert.Contains("idx_symbol_refs_name_nocase_file", indexes);
        Assert.Contains("idx_symbol_refs_container_nocase_kind", indexes);
        Assert.Contains("idx_symbol_refs_symbol_name_folded_kind", indexes);
        Assert.Contains("idx_symbol_refs_symbol_name_folded_file", indexes);
        Assert.Contains("idx_symbol_refs_container_name_folded_kind", indexes);

        AssertIndexColumns(_db.Connection, "idx_symbol_refs_name_nocase_kind", [("symbol_name", "NOCASE"), ("reference_kind", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_name_nocase_file", [("symbol_name", "NOCASE"), ("file_id", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_container_nocase_kind", [("container_name", "NOCASE"), ("reference_kind", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_symbol_name_folded_kind", [("symbol_name_folded", "BINARY"), ("reference_kind", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_symbol_name_folded_file", [("symbol_name_folded", "BINARY"), ("file_id", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_container_name_folded_kind", [("container_name_folded", "BINARY"), ("reference_kind", "BINARY")]);
    }

    [Fact]
    public void TryMigrateForRead_CreatesReferenceCompositeIndexesForGraphLookups()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_legacy_index_test_{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE files (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        path TEXT NOT NULL UNIQUE
                    );
                    CREATE TABLE symbols (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        kind TEXT,
                        name TEXT,
                        line INTEGER
                    );
                    CREATE TABLE symbol_references (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        symbol_name TEXT,
                        reference_kind TEXT,
                        line INTEGER,
                        column_number INTEGER,
                        context TEXT,
                        container_kind TEXT,
                        container_name TEXT
                    );";
                cmd.ExecuteNonQuery();
            }

            using var db = new DbContext(dbPath);
            db.TryMigrateForRead();
            var indexes = ReadIndexNames(db.Connection, "symbol_references");

            Assert.Contains("idx_symbol_refs_name_kind", indexes);
            Assert.Contains("idx_symbol_refs_name_file", indexes);
            Assert.Contains("idx_symbol_refs_name_nocase_kind", indexes);
            Assert.Contains("idx_symbol_refs_name_nocase_file", indexes);
            Assert.Contains("idx_symbol_refs_container_nocase_kind", indexes);
            Assert.Contains("idx_symbol_refs_symbol_name_folded_kind", indexes);
            Assert.Contains("idx_symbol_refs_symbol_name_folded_file", indexes);
            Assert.Contains("idx_symbol_refs_container_name_folded_kind", indexes);

            AssertIndexColumns(db.Connection, "idx_symbol_refs_container_nocase_kind", [("container_name", "NOCASE"), ("reference_kind", "BINARY")]);
            AssertIndexColumns(db.Connection, "idx_symbol_refs_container_name_folded_kind", [("container_name_folded", "BINARY"), ("reference_kind", "BINARY")]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                try
                {
                    File.Delete(dbPath);
                }
                catch (IOException) when (OperatingSystem.IsWindows())
                {
                    SqliteConnection.ClearAllPools();
                    File.Delete(dbPath);
                }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
                {
                    SqliteConnection.ClearAllPools();
                    File.Delete(dbPath);
                }
            }
        }
    }

    [Fact]
    public void Constructor_WritableOpenRejectsNewerUserVersion()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_newer_schema_test_{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"PRAGMA user_version = {DbContext.CurrentSchemaVersion + 1}";
                cmd.ExecuteNonQuery();
            }

            var ex = Assert.Throws<CodeIndexException>(() => new DbContext(dbPath));

            Assert.Equal(CodeIndex.Cli.CommandErrorCodes.SchemaTooNew, ex.Code);
            Assert.Equal(CodeIndexExceptionCategory.Database, ex.Category);
            Assert.Equal(dbPath, ex.Path);
            Assert.Contains("newer cdidx", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rebuild the index", ex.Hint, StringComparison.OrdinalIgnoreCase);

            using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
            verifyConnection.Open();
            using var verifyJournalMode = verifyConnection.CreateCommand();
            verifyJournalMode.CommandText = "PRAGMA journal_mode";
            var journalMode = Assert.IsType<string>(verifyJournalMode.ExecuteScalar());
            Assert.False(string.Equals("wal", journalMode, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                try
                {
                    File.Delete(dbPath);
                }
                catch (IOException) when (OperatingSystem.IsWindows())
                {
                    SqliteConnection.ClearAllPools();
                    File.Delete(dbPath);
                }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
                {
                    SqliteConnection.ClearAllPools();
                    File.Delete(dbPath);
                }
            }
        }
    }

    [Fact]
    public void TryMigrateForRead_EnforcesForeignKeysAfterAddingReferenceLineColumn()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_legacy_fk_test_{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE files (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        path TEXT NOT NULL UNIQUE
                    );
                    CREATE TABLE symbols (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        kind TEXT,
                        name TEXT,
                        line INTEGER
                    );
                    CREATE TABLE symbol_references (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        symbol_name TEXT,
                        reference_kind TEXT,
                        line INTEGER,
                        column_number INTEGER,
                        context TEXT,
                        container_kind TEXT,
                        container_name TEXT
                    );";
                cmd.ExecuteNonQuery();
            }

            using var db = new DbContext(dbPath);
            db.TryMigrateForRead();

            using (var fkCheck = db.Connection.CreateCommand())
            {
                fkCheck.CommandText = "PRAGMA foreign_keys";
                Assert.Equal(1L, Convert.ToInt64(fkCheck.ExecuteScalar()));
            }

            using (var insertFile = db.Connection.CreateCommand())
            {
                insertFile.CommandText = "INSERT INTO files(path) VALUES ('src/Use.cs')";
                insertFile.ExecuteNonQuery();
            }

            using var insertReference = db.Connection.CreateCommand();
            insertReference.CommandText = @"
                INSERT INTO symbol_references(file_id, symbol_name, reference_kind, line, column_number, context, reference_line_id)
                VALUES (1, 'MissingLine', 'call', 1, 1, 'MissingLine()', 999)";
            var ex = Assert.Throws<SqliteException>(() => insertReference.ExecuteNonQuery());
            Assert.Equal(19, ex.SqliteErrorCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                try
                {
                    File.Delete(dbPath);
                }
                catch (IOException) when (OperatingSystem.IsWindows())
                {
                    SqliteConnection.ClearAllPools();
                    File.Delete(dbPath);
                }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
                {
                    SqliteConnection.ClearAllPools();
                    File.Delete(dbPath);
                }
            }
        }
    }

    [Fact]
    public void Constructor_ConfiguresWalDurabilityPragmas()
    {
        Assert.Equal("wal", ExecuteScalarString("PRAGMA journal_mode"));
        Assert.Equal(1L, ExecuteScalarLong("PRAGMA synchronous"));
        Assert.Equal(DbContext.DefaultWalAutocheckpointPages, ExecuteScalarLong("PRAGMA wal_autocheckpoint"));
    }

    [Fact]
    public void UpsertFile_InsertsAndReturnsId()
    {
        var file = new FileRecord
        {
            Path = "src/main.py",
            Lang = "python",
            Size = 100,
            Lines = 10,
            Checksum = "abc123",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var id = _writer.UpsertFile(file);
        Assert.True(id > 0);
    }

    [Fact]
    public void UpsertFile_ReplacesOnConflict()
    {
        // Same path should replace (not duplicate)
        // 同一パスは置換される（重複しない）
        var file1 = new FileRecord
        {
            Path = "src/app.py", Lang = "python", Size = 100, Lines = 10,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var file2 = new FileRecord
        {
            Path = "src/app.py", Lang = "python", Size = 200, Lines = 20,
            Modified = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        _writer.UpsertFile(file1);
        _writer.UpsertFile(file2);

        var (count, _, _, _) = _writer.GetCounts();
        Assert.Equal(1, count);
    }

    [Fact]
    public void GetUnchangedFileId_ReturnIdIfUnchanged()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/lib.py", Lang = "python", Size = 50, Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(file);

        // Same modified time should return the ID
        // 同一更新日時ならIDを返す
        var id = _writer.GetUnchangedFileId("src/lib.py", modified);
        Assert.NotNull(id);

        // Different modified time should return null
        // 異なる更新日時ならnullを返す
        var id2 = _writer.GetUnchangedFileId("src/lib.py", modified.AddHours(1));
        Assert.Null(id2);
    }

    [Fact]
    public void GetUnchangedFileId_WithNullChecksumUsesModifiedAndSize()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/size.py", Lang = "python", Size = 50, Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(file);

        var id = _writer.GetUnchangedFileId("src/size.py", modified, checksum: null, size: 50);
        Assert.NotNull(id);

        var changedSizeId = _writer.GetUnchangedFileId("src/size.py", modified, checksum: null, size: 51);
        Assert.Null(changedSizeId);
    }

    [Fact]
    public void GetUnchangedFileId_ReturnsNullWhenLanguageExtractorVersionIsStale()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/lib.py", Lang = "python", Size = 50, Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(file);
        _writer.SetMeta(DbContext.GetSymbolExtractorVersionMetaKey("python"), "0");

        var id = _writer.GetUnchangedFileId("src/lib.py", modified, language: "python");

        Assert.Null(id);
    }

    [Fact]
    public void GetUnchangedFileId_MatchesByChecksumWhenTimestampDiffers()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var checksum = "abc123def456";
        var file = new FileRecord
        {
            Path = "src/checksum.py", Lang = "python", Size = 50, Lines = 5,
            Modified = modified, Checksum = checksum,
        };
        _writer.UpsertFile(file);

        // Different timestamp but same checksum should return the ID (e.g. git checkout)
        // タイムスタンプ異なるがチェックサム一致ならIDを返す（例: git checkout）
        var newModified = modified.AddHours(1);
        var id = _writer.GetUnchangedFileId("src/checksum.py", newModified, checksum);
        Assert.NotNull(id);

        // Different timestamp AND different checksum should return null
        // タイムスタンプもチェックサムも異なるならnullを返す
        var id2 = _writer.GetUnchangedFileId("src/checksum.py", newModified.AddHours(1), "different_checksum");
        Assert.Null(id2);
    }

    [Fact]
    public void GetUnchangedFileId_UpdatesGeneratedMarkerOnReusableRows()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var checksum = "generated-checksum";
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/generated.g.cs",
            Lang = "csharp",
            Size = 50,
            Lines = 2,
            Modified = modified,
            Checksum = checksum,
            Generated = false,
        });

        var id = _writer.GetUnchangedFileId("src/generated.g.cs", modified, checksum, language: "csharp", generated: true);

        Assert.NotNull(id);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT generated FROM files WHERE path = 'src/generated.g.cs'";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void GetUnchangedFileId_ReturnsNullWhenTimestampMatchesButChecksumDiffers()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/coarse-time.py", Lang = "python", Size = 50, Lines = 5,
            Modified = modified, Checksum = "first_checksum",
        };
        _writer.UpsertFile(file);

        var id = _writer.GetUnchangedFileId("src/coarse-time.py", modified, "second_checksum");

        Assert.Null(id);
    }

    [Fact]
    public void PurgeStaleFilesSharingChecksum_RemovesDeletedRenameRowsOnly()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"cdidx_checksum_purge_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src/current.py"), "print('same')\n");
            File.WriteAllText(Path.Combine(projectRoot, "src/duplicate.py"), "print('same')\n");

            var modified = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
            var currentId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/current.py", Lang = "python", Size = 14, Lines = 1,
                Checksum = "same_checksum", Modified = modified,
            });
            var staleId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/renamed-away.py", Lang = "python", Size = 14, Lines = 1,
                Checksum = "same_checksum", Modified = modified,
            });
            var duplicateId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/duplicate.py", Lang = "python", Size = 14, Lines = 1,
                Checksum = "same_checksum", Modified = modified,
            });
            _writer.InsertChunks([
                new() { FileId = currentId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "current" },
                new() { FileId = staleId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "stale" },
                new() { FileId = duplicateId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "duplicate" },
            ]);

            var purged = _writer.PurgeStaleFilesSharingChecksum(projectRoot, "src/current.py", "same_checksum");

            Assert.Equal(1, purged);
            Assert.True(_writer.HasFileAtPath("src/current.py"));
            Assert.False(_writer.HasFileAtPath("src/renamed-away.py"));
            Assert.True(_writer.HasFileAtPath("src/duplicate.py"));
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunks";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
                TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void InsertChunks_InsertsAndPopulatesFts()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/test.py", Lang = "python", Size = 100, Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var chunks = new List<ChunkRecord>
        {
            new() { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 10, Content = "def authenticate(user):" },
        };
        _writer.InsertChunks(chunks);

        // Verify FTS search works / FTS検索が動作することを確認
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT content FROM fts_chunks WHERE fts_chunks MATCH 'authenticate'";
        var result = cmd.ExecuteScalar() as string;
        Assert.NotNull(result);
        Assert.Contains("authenticate", result);
    }

    [Fact]
    public void InsertChunks_MultiRowValuesPopulatesFtsForEveryRow()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/multi.py", Lang = "python", Size = 300, Lines = 300,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var chunks = Enumerable.Range(0, 4)
            .Select(i => new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = i,
                StartLine = i + 1,
                EndLine = i + 1,
                Content = $"def multirow_token_{i}(): pass",
            })
            .ToList();

        _writer.InsertChunks(chunks);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'multirow_token_3'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void InsertSymbols_InsertsCorrectly()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/svc.py", Lang = "python", Size = 50, Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var symbols = new List<SymbolRecord>
        {
            new() { FileId = fileId, Kind = "function", Name = "process", Line = 1 },
            new() { FileId = fileId, Kind = "class", Name = "Service", Line = 5 },
        };
        _writer.InsertSymbols(symbols);

        var (_, _, symbolCount, _) = _writer.GetCounts();
        Assert.Equal(2, symbolCount);
    }

    [Fact]
    public void InsertSymbols_ChunksLargeInputUnderSqlVariableLimit()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/symbols.py", Lang = "python", Size = 1000, Lines = 1000,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var symbols = Enumerable.Range(0, 120)
            .Select(i => new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = $"fn_{i}",
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
            })
            .ToList();

        _writer.InsertSymbols(symbols);

        var (_, _, symbolCount, _) = _writer.GetCounts();
        Assert.Equal(120, symbolCount);
    }

    [Fact]
    public void DeleteFileData_RemovesChunksAndSymbols()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/del.py", Lang = "python", Size = 50, Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertChunks([new() { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 5, Content = "test" }]);
        _writer.InsertSymbols([new() { FileId = fileId, Kind = "function", Name = "test", Line = 1 }]);

        _writer.DeleteFileData(fileId);

        var (_, chunkCount, symbolCount, referenceCount) = _writer.GetCounts();
        Assert.Equal(0, chunkCount);
        Assert.Equal(0, symbolCount);
        Assert.Equal(0, referenceCount);
    }

    [Fact]
    public void InsertReferences_InsertsCorrectly()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/ref.py", Lang = "python", Size = 50, Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertReferences([
            new ReferenceRecord { FileId = fileId, SymbolName = "authenticate", ReferenceKind = "call", Line = 2, Column = 12, Context = "return authenticate(user, password)", ContainerKind = "function", ContainerName = "login" },
        ]);

        var (_, _, _, referenceCount) = _writer.GetCounts();
        Assert.Equal(1, referenceCount);
    }

    [Fact]
    public void InsertReferences_ChunksLargeInputAndDeduplicatesReferenceLines()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/refs.py", Lang = "python", Size = 1000, Lines = 1000,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var references = Enumerable.Range(0, 120)
            .Select(i => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = $"callee_{i}",
                ReferenceKind = "call",
                Line = i % 10 + 1,
                Column = 4,
                Context = $"callee_{i}()",
                ContainerKind = "function",
                ContainerName = "caller",
            })
            .ToList();

        _writer.InsertReferences(references);

        var (_, _, _, referenceCount) = _writer.GetCounts();
        Assert.Equal(120, referenceCount);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM reference_lines";
        Assert.Equal(10L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void RebuildTypeScriptAugmentationReferences_LinksMergedInterfacesOnly()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"cdidx_ts_aug_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, "src/module-c.ts"), "export {}\ninterface Ambient {}\n");
        File.WriteAllText(Path.Combine(projectRoot, "src/module-d.ts"), "import \"./setup\";\ninterface Ambient {}\n");
        File.WriteAllText(Path.Combine(projectRoot, "src/express-a.ts"), "declare module \"express\" { interface Request { user: string } }\n");
        File.WriteAllText(Path.Combine(projectRoot, "src/express-b.ts"), "declare module \"express\" { interface Request { account: string } }\n");

        var firstFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/a.ts", Lang = "typescript", Size = 80, Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var secondFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/b.ts", Lang = "typescript", Size = 80, Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var thirdFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/c.ts", Lang = "typescript", Size = 80, Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var moduleOneFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/module-a.ts", Lang = "typescript", Size = 80, Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var moduleTwoFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/module-b.ts", Lang = "typescript", Size = 80, Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var moduleMarkerFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/module-c.ts", Lang = "typescript", Size = 80, Lines = 2,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var sideEffectImportFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/module-d.ts", Lang = "typescript", Size = 80, Lines = 2,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var ambientGlobalFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/ambient-global.ts", Lang = "typescript", Size = 80, Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var ambientModuleFirstFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/express-a.ts", Lang = "typescript", Size = 80, Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var ambientModuleSecondFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/express-b.ts", Lang = "typescript", Size = 80, Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertSymbols([
            new SymbolRecord { FileId = firstFileId, Kind = "interface", Name = "Widget", Line = 1, StartLine = 1, StartColumn = 7, EndLine = 3, Signature = "interface Widget { a: number }" },
            new SymbolRecord { FileId = secondFileId, Kind = "interface", Name = "Widget", Line = 1, StartLine = 1, StartColumn = 17, EndLine = 3, Signature = "declare global { interface Widget { b: string } }" },
            new SymbolRecord { FileId = firstFileId, Kind = "import", Name = "Options", Line = 4, StartLine = 4, StartColumn = 5, EndLine = 4, Signature = "type Options = { a: number }" },
            new SymbolRecord { FileId = secondFileId, Kind = "import", Name = "Options", Line = 4, StartLine = 4, StartColumn = 5, EndLine = 4, Signature = "type Options = { b: string }" },
            new SymbolRecord { FileId = thirdFileId, Kind = "interface", Name = "LocalOnly", Line = 1, StartLine = 1, StartColumn = 11, EndLine = 1, Signature = "interface LocalOnly {}" },
            new SymbolRecord { FileId = moduleOneFileId, Kind = "interface", Name = "Props", Line = 2, StartLine = 2, StartColumn = 17, EndLine = 2, Signature = "export interface Props { a: number }", Visibility = "export" },
            new SymbolRecord { FileId = moduleTwoFileId, Kind = "interface", Name = "Props", Line = 2, StartLine = 2, StartColumn = 17, EndLine = 2, Signature = "export interface Props { b: string }", Visibility = "export" },
            new SymbolRecord { FileId = moduleMarkerFileId, Kind = "interface", Name = "Ambient", Line = 2, StartLine = 2, StartColumn = 11, EndLine = 2, Signature = "interface Ambient {}" },
            new SymbolRecord { FileId = sideEffectImportFileId, Kind = "interface", Name = "Ambient", Line = 2, StartLine = 2, StartColumn = 11, EndLine = 2, Signature = "interface Ambient {}" },
            new SymbolRecord { FileId = ambientGlobalFileId, Kind = "interface", Name = "Ambient", Line = 1, StartLine = 1, StartColumn = 11, EndLine = 1, Signature = "interface Ambient {}" },
            new SymbolRecord { FileId = ambientModuleFirstFileId, Kind = "interface", Name = "Request", Line = 1, StartLine = 1, StartColumn = 28, EndLine = 1, Signature = "interface Request { user: string }", ContainerName = "\"express\"" },
            new SymbolRecord { FileId = ambientModuleSecondFileId, Kind = "interface", Name = "Request", Line = 1, StartLine = 1, StartColumn = 28, EndLine = 1, Signature = "interface Request { account: string }", ContainerName = "\"express\"" },
        ]);

        var inserted = _writer.RebuildTypeScriptAugmentationReferences(projectRoot);

        Assert.Equal(4, inserted);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT symbol_name, container_kind, COUNT(*)
            FROM symbol_references
            WHERE reference_kind = 'augmentation'
            GROUP BY symbol_name, container_kind
            ORDER BY symbol_name, container_kind";

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Request", reader.GetString(0));
        Assert.Equal("interface", reader.GetString(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.True(reader.Read());
        Assert.Equal("Widget", reader.GetString(0));
        Assert.Equal("interface", reader.GetString(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.False(reader.Read());
    }

    private static HashSet<string> ReadIndexNames(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name=@tableName";
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            indexes.Add(reader.GetString(0));
        return indexes;
    }

    private static void AssertIndexColumns(SqliteConnection connection, string indexName, IReadOnlyList<(string Name, string Collation)> expected)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA index_xinfo('{indexName.Replace("'", "''")}')";

        var actual = new List<(string Name, string Collation)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var isKey = reader.GetInt32(5) == 1;
            if (!isKey)
                continue;
            actual.Add((reader.GetString(2), reader.GetString(4)));
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void InsertReferences_DeduplicatesReferenceLinesByFileAndLine()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/ref_lines.py", Lang = "python", Size = 50, Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertReferences([
            new ReferenceRecord { FileId = fileId, SymbolName = "authenticate", ReferenceKind = "call", Line = 2, Column = 4, Context = "return authenticate(user, password)", ContainerKind = "function", ContainerName = "login" },
            new ReferenceRecord { FileId = fileId, SymbolName = "authorize", ReferenceKind = "call", Line = 2, Column = 16, Context = "return authenticate(user, password)", ContainerKind = "function", ContainerName = "login" },
            new ReferenceRecord { FileId = fileId, SymbolName = "authenticate", ReferenceKind = "call", Line = 2, Column = 28, Context = "return authenticate(user, password)", ContainerKind = "function", ContainerName = "login" },
        ]);

        using var cmd = _db.Connection.CreateCommand();
        cmd.Parameters.AddWithValue("@fileId", fileId);

        cmd.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId AND line = 2";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId";
        Assert.Equal(3L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId AND context IS NOT NULL";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(DISTINCT reference_line_id) FROM symbol_references WHERE file_id = @fileId";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT context FROM reference_lines WHERE file_id = @fileId AND line = 2";
        Assert.Equal("return authenticate(user, password)", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void InsertReferences_TypeScriptConstAssertion_RoundTripsThroughSql()
    {
        const string content = """
            const tuple = ["alpha", "beta"] as const;
            """;
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/const-assertion.ts",
            Lang = "typescript",
            Size = content.Length,
            Lines = 1,
            Modified = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc),
        });
        var symbols = SymbolExtractor.Extract(fileId, "typescript", content);
        var references = ReferenceExtractor.Extract(fileId, "typescript", content, symbols);

        _writer.InsertReferences(references);

        using var cmd = _db.Connection.CreateCommand();
        cmd.Parameters.AddWithValue("@fileId", fileId);
        cmd.CommandText = """
            SELECT symbol_name, reference_kind
            FROM symbol_references
            WHERE file_id = @fileId
            ORDER BY line, column_number, reference_kind
            """;
        var rows = new List<(string SymbolName, string ReferenceKind)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));

        Assert.Contains(("const", "const_assertion"), rows);
        Assert.Contains(("\"alpha\"", "type_reference"), rows);
        Assert.Contains(("\"beta\"", "type_reference"), rows);
    }

    [Fact]
    public void InsertReferences_RollsBackChunkOnPartialFailureUnderOuterTransaction()
    {
        // Regression: #1518 — under an outer transaction, a mid-chunk
        // symbol_references INSERT failure must not leave orphan reference_lines.
        // 外側トランザクション下で symbol_references INSERT が失敗した場合、
        // 同じチャンク内で挿入済みの reference_lines が孤児として残ってはならない。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/partial.py", Lang = "python", Size = 50, Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        using (var trig = _db.Connection.CreateCommand())
        {
            trig.CommandText = @"CREATE TRIGGER fail_symbol_marker BEFORE INSERT ON symbol_references
                WHEN NEW.symbol_name = 'FAIL_ME' BEGIN
                    SELECT RAISE(ABORT, 'forced symbol_references failure');
                END";
            trig.ExecuteNonQuery();
        }

        try
        {
            using var outer = _writer.BeginTransaction();
            Assert.Throws<SqliteException>(() => _writer.InsertReferences([
                new ReferenceRecord { FileId = fileId, SymbolName = "ok_before", ReferenceKind = "call", Line = 1, Column = 1, Context = "ok line", ContainerKind = "function", ContainerName = "c" },
                new ReferenceRecord { FileId = fileId, SymbolName = "FAIL_ME", ReferenceKind = "call", Line = 99, Column = 1, Context = "fail line", ContainerKind = "function", ContainerName = "c" },
            ]));
            // Outer transaction must still be usable; its commit must not persist
            // any reference_lines from the rolled-back chunk.
            // 外側トランザクションはロールバック後も生存しており、commit してもチャンクの
            // reference_lines は残らないこと。
            outer.Commit();
        }
        finally
        {
            using var drop = _db.Connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER IF EXISTS fail_symbol_marker";
            drop.ExecuteNonQuery();
        }

        using var refLineCount = _db.Connection.CreateCommand();
        refLineCount.Parameters.AddWithValue("@fileId", fileId);
        refLineCount.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId";
        Assert.Equal(0L, (long)refLineCount.ExecuteScalar()!);

        using var refCount = _db.Connection.CreateCommand();
        refCount.Parameters.AddWithValue("@fileId", fileId);
        refCount.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId";
        Assert.Equal(0L, (long)refCount.ExecuteScalar()!);

        using var orphanCount = _db.Connection.CreateCommand();
        orphanCount.Parameters.AddWithValue("@fileId", fileId);
        orphanCount.CommandText = @"SELECT COUNT(*) FROM reference_lines rl
            WHERE rl.file_id = @fileId
              AND NOT EXISTS (SELECT 1 FROM symbol_references sr WHERE sr.reference_line_id = rl.id)";
        Assert.Equal(0L, (long)orphanCount.ExecuteScalar()!);
    }

    [Fact]
    public void CleanExistingFileData_PreventsFtsOrphans()
    {
        // Insert a file with chunks (populates FTS) / ファイルとチャンク（FTS含む）を挿入
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/orphan.py", Lang = "python", Size = 50, Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new() { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 5, Content = "def hello_orphan_test(): pass" }]);
        _writer.InsertReferences([new() { FileId = fileId, SymbolName = "hello_orphan_test", ReferenceKind = "call", Line = 1, Column = 5, Context = "def hello_orphan_test(): pass", ContainerKind = "function", ContainerName = "hello_orphan_test" }]);

        // Verify FTS has the entry / FTSにエントリがあることを確認
        using var cmd1 = _db.Connection.CreateCommand();
        cmd1.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'hello_orphan_test'";
        Assert.Equal(1L, (long)cmd1.ExecuteScalar()!);

        // Clean existing data then re-upsert (simulates re-indexing)
        // 既存データを掃除してから再upsert（再インデックスをシミュレート）
        _writer.CleanExistingFileData("src/orphan.py");
        var newId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/orphan.py", Lang = "python", Size = 60, Lines = 6,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(1),
        });
        _writer.InsertChunks([new() { FileId = newId, ChunkIndex = 0, StartLine = 1, EndLine = 6, Content = "def world_replacement(): pass" }]);

        // Old FTS entry should be gone, new one should exist
        // 旧FTSエントリは消え、新エントリが存在するはず
        using var cmd2 = _db.Connection.CreateCommand();
        cmd2.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'hello_orphan_test'";
        Assert.Equal(0L, (long)cmd2.ExecuteScalar()!);

        using var cmd3 = _db.Connection.CreateCommand();
        cmd3.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'world_replacement'";
        Assert.Equal(1L, (long)cmd3.ExecuteScalar()!);

        using var cmd4 = _db.Connection.CreateCommand();
        cmd4.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId";
        cmd4.Parameters.AddWithValue("@fileId", fileId);
        Assert.Equal(0L, (long)cmd4.ExecuteScalar()!);
    }

    [Fact]
    public void PurgeStaleFiles_RemovesDeletedFiles()
    {
        // Simulate branch switch: insert a file, then purge when file doesn't exist
        // ブランチ切り替えをシミュレート: ファイルを挿入後、存在しないファイルをパージ
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_purge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create a real file and a "ghost" file entry
            // 実在するファイルと「ゴースト」ファイルエントリを作成
            var realFile = Path.Combine(tempDir, "real.py");
            File.WriteAllText(realFile, "x = 1");

            _writer.UpsertFile(new FileRecord
            {
                Path = "real.py", Lang = "python", Size = 5, Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            _writer.UpsertFile(new FileRecord
            {
                Path = "ghost.py", Lang = "python", Size = 10, Lines = 2,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });

            var (beforeCount, _, _, _) = _writer.GetCounts();
            Assert.Equal(2, beforeCount);

            var purged = _writer.PurgeStaleFiles(tempDir);
            Assert.Equal(1, purged);

            var (afterCount, _, _, _) = _writer.GetCounts();
            Assert.Equal(1, afterCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PurgeFilesOutsideRetainedSetWithinListedDirectories_PurgesDeepDescendantsUnderSymlinkPrunedDirectory()
    {
        // Regression for #190 follow-up: earlier symlink-following runs can leave entries like
        // "sub/parent_loop/nested/deep.py" whose immediate parent ("sub/parent_loop/nested") is not in
        // the current scan's listedDirectories. The partial-purge walker must still remove them because
        // the symlink directory itself is authoritatively skipped in the current scan.
        // #190 追補の回帰: 過去の symlink 追従により "sub/parent_loop/nested/deep.py" のような深い
        // 子孫エントリが残るが、その immediate parent は今回の scan の listedDirectories には含まれない。
        // symlink ディレクトリ自身を authoritative に skip している以上、partial-purge は
        // この子孫を確実に削除しなければならない。
        _writer.UpsertFile(new FileRecord { Path = "sub/parent_loop/shallow.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });
        _writer.UpsertFile(new FileRecord { Path = "sub/parent_loop/nested/deep.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });
        _writer.UpsertFile(new FileRecord { Path = "sub/parent_loop_sibling/keep.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });
        _writer.UpsertFile(new FileRecord { Path = "sub/foo.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });

        var retained = new HashSet<string>(StringComparer.Ordinal) { "sub/foo.py", "sub/parent_loop_sibling/keep.py" };
        var listedDirectories = new HashSet<string>(StringComparer.Ordinal) { string.Empty, "sub", "sub/parent_loop", "sub/parent_loop_sibling" };
        var symlinkPrunedDirectories = new HashSet<string>(StringComparer.Ordinal) { "sub/parent_loop" };

        var purged = _writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(retained, listedDirectories, symlinkPrunedDirectories);

        Assert.Equal(2, purged);
        Assert.False(_writer.HasFileAtPath("sub/parent_loop/shallow.py"));
        Assert.False(_writer.HasFileAtPath("sub/parent_loop/nested/deep.py"));
        Assert.True(_writer.HasFileAtPath("sub/parent_loop_sibling/keep.py"));
        Assert.True(_writer.HasFileAtPath("sub/foo.py"));
    }

    [Fact]
    public void PurgeFilesOutsideRetainedSetWithinListedDirectories_DoesNotConfuseSymlinkPrefixWithSiblingDirectory()
    {
        // Guard: "sub/parent_loop" prune prefix must not match "sub/parent_loop_x/inside.py".
        // ガード: prune prefix "sub/parent_loop" は "sub/parent_loop_x/inside.py" を巻き込まない。
        _writer.UpsertFile(new FileRecord { Path = "sub/parent_loop_x/inside.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });

        var retained = new HashSet<string>(StringComparer.Ordinal) { "sub/parent_loop_x/inside.py" };
        var listedDirectories = new HashSet<string>(StringComparer.Ordinal) { "sub", "sub/parent_loop_x" };
        var symlinkPrunedDirectories = new HashSet<string>(StringComparer.Ordinal) { "sub/parent_loop" };

        var purged = _writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(retained, listedDirectories, symlinkPrunedDirectories);

        Assert.Equal(0, purged);
        Assert.True(_writer.HasFileAtPath("sub/parent_loop_x/inside.py"));
    }

    [Fact]
    public void DropAll_RemovesAllTables()
    {
        // Insert some data, then drop all
        // データを挿入してから全削除
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/x.py", Lang = "python", Size = 10, Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _db.DropAll();
        _db.InitializeSchema();

        var (files, chunks, symbols, references) = _writer.GetCounts();
        Assert.Equal(0, files);
        Assert.Equal(0, chunks);
        Assert.Equal(0, symbols);
        Assert.Equal(0, references);
    }

    [Fact]
    public void DeleteFileByPath_RemovesFileAndData()
    {
        // Insert a file with chunks and symbols, then delete by path
        // ファイルとチャンク・シンボルを挿入し、パスで削除
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/remove_me.py", Lang = "python", Size = 50, Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertChunks([new() { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 5, Content = "def foo(): pass" }]);
        _writer.InsertSymbols([new() { FileId = fileId, Kind = "function", Name = "foo", Line = 1 }]);

        var result = _writer.DeleteFileByPath("src/remove_me.py");
        Assert.True(result);

        var (files, chunks, symbols, references) = _writer.GetCounts();
        Assert.Equal(0, files);
        Assert.Equal(0, chunks);
        Assert.Equal(0, symbols);
        Assert.Equal(0, references);
    }

    [Fact]
    public void DeleteFileByPath_ReturnsFalseIfNotFound()
    {
        // Deleting a non-existent path returns false
        // 存在しないパスの削除はfalseを返す
        var result = _writer.DeleteFileByPath("nonexistent/file.py");
        Assert.False(result);
    }

    [Fact]
    public void DeleteFileByPath_DoesNotAffectOtherFiles()
    {
        // Deleting one file should not affect another
        // 1ファイルの削除は他のファイルに影響しない
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/keep.py", Lang = "python", Size = 50, Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/delete.py", Lang = "python", Size = 30, Lines = 3,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.DeleteFileByPath("src/delete.py");

        var (files, _, _, _) = _writer.GetCounts();
        Assert.Equal(1, files);
    }

    [Fact]
    public void MarkFoldReady_StampsFoldReadyWhenAllRowsBackfilled()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/fold_ok.py", Lang = "python", Size = 30, Lines = 3,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([
            new SymbolRecord { FileId = fileId, Kind = "function", Name = "Straße", Line = 1, StartLine = 1, EndLine = 1 },
        ]);

        var stamped = _writer.MarkFoldReady();

        Assert.True(stamped);
        Assert.Equal(DbContext.FoldReadyFlag, _db.GetUserVersion() & DbContext.FoldReadyFlag);
    }

    [Fact]
    public void MarkFoldReady_LeavesFoldReadyUnsetWhenNullFoldedRowExists()
    {
        // Reproduces issue #1535: a concurrent writer inserting a NULL-folded row between
        // an upfront verify and the FoldReady stamp can leave readers on the fold path with
        // some rows still NULL. The fix re-verifies inside MarkFoldReady's BEGIN IMMEDIATE so
        // this stamp is skipped and readers stay on NOCASE until backfill_fold is re-run.
        // issue #1535 の再現: 上位の verify 後に concurrent writer が NULL 行を差し込んだ場合、
        // 修正後の MarkFoldReady は再検証で stamp を取りやめ、reader を NOCASE に保つ。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/fold_partial.py", Lang = "python", Size = 30, Lines = 3,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([
            new SymbolRecord { FileId = fileId, Kind = "function", Name = "Straße", Line = 1, StartLine = 1, EndLine = 1 },
        ]);

        // Simulate a concurrent NULL-folded insert that slipped in after the caller's
        // upfront AllFoldedColumnsBackfilled check returned true.
        // 上位の verify 直後に concurrent writer が NULL 行を入れたシナリオを再現する。
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE symbols SET name_folded = NULL";
            cmd.ExecuteNonQuery();
        }

        var stamped = _writer.MarkFoldReady();

        Assert.False(stamped);
        Assert.Equal(0, _db.GetUserVersion() & DbContext.FoldReadyFlag);
        Assert.Null(_db.GetMetaString("fold_key_version"));
        Assert.Null(_db.GetMetaString("fold_key_fingerprint"));
    }

    public void Dispose()
    {
        _db.Dispose();
        DeleteDbPath();
    }

    private void DeleteDbPath()
    {
        DeleteDbFiles(_dbPath);
    }

    private static void DeleteDbFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (!File.Exists(path))
                continue;

            DeleteDbFile(path);
        }
    }

    private static void DeleteDbFile(string dbPath)
    {
        try
        {
            File.Delete(dbPath);
        }
        catch (IOException)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
        catch (UnauthorizedAccessException)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private string ExecuteScalarString(string sql)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private long ExecuteScalarLong(string sql)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
