using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for database corruption recovery and graceful degradation.
/// DB破損からの復旧とグレースフル劣化のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class DbRecoveryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDisposable _sqlitePoolOwner;
    private bool _disposed;

    public DbRecoveryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_recovery_{Guid.NewGuid():N}.db");
        _sqlitePoolOwner = SqlitePoolCleanup.EnterExclusiveOwner();
    }

    [Fact]
    public void TryMigrateForRead_OnCorruptedDb_DoesNotCrash()
    {
        // Write garbage to simulate a corrupted database / ゴミデータを書き込んで破損DBをシミュレート
        File.WriteAllText(_dbPath, "NOT A SQLITE DATABASE - corrupted content here");

        // Opening a corrupted DB should throw at the SQLite level, not crash the process
        // 破損DBを開くとSQLiteレベルで例外が出るがプロセスはクラッシュしない
        Assert.ThrowsAny<Exception>(() =>
        {
            using var db = new DbContext(_dbPath);
            db.TryMigrateForRead();
        });
    }

    [Fact]
    public void RebuildAfterCorruption_CreatesCleanIndex()
    {
        // Create a valid DB first / まず有効なDBを作成
        using (var db = new DbContext(_dbPath))
        {
            db.InitializeSchema();
        }

        // Release all pooled connections so Windows can overwrite the file
        // プール済み接続をすべて解放し、Windowsでファイル上書きを可能にする
        SqlitePoolCleanup.ClearPoolsForWindowsFileRelease(callerOwnsExclusiveAccess: true);

        // Corrupt it by overwriting / 上書きして破損させる
        File.WriteAllBytes(_dbPath, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        // Delete and recreate — simulates user running --rebuild after corruption
        // 削除して再作成 — ユーザーが破損後に --rebuild を実行するシミュレート
        File.Delete(_dbPath);
        using var newDb = new DbContext(_dbPath);
        newDb.InitializeSchema();

        var writer = new DbWriter(newDb.Connection);
        var (files, _, _, _) = writer.GetCounts();
        Assert.Equal(0, files); // Clean slate / クリーンな状態
    }

    [Fact]
    public void QueryOnMissingDb_ReturnsProperExitCode()
    {
        // Explicit missing --db values are rejected as usage errors before opening a reader.
        // 明示された存在しない --db は reader を開く前に usage error として拒否する。
        var nonExistentDb = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.db");
        lock (TestConsoleLock.Gate)
        {
            var originalErr = Console.Error;
            using var errWriter = new StringWriter();
            try
            {
                Console.SetError(errWriter);
                var exitCode = QueryCommandRunner.RunStatus(["--db", nonExistentDb], new System.Text.Json.JsonSerializerOptions());
                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Contains("does not point to an existing database file", errWriter.ToString());
            }
            finally
            {
                Console.SetError(originalErr);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sqlitePoolOwner.Dispose();
        TestProjectHelper.DeleteFile(_dbPath);
    }
}
