using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Mcp;

public partial class McpServer : IDisposable
{


    // Tool implementations are in McpToolHandlers.cs / ツール実装は McpToolHandlers.cs に分離

    // --- DB helper / DBヘルパー ---

    private JsonNode WithDbReader(JsonNode? id, JsonNode? args, Func<DbReader, JsonNode> action)
    {
        var isolateRequestDb = _isolateDbForCurrentRequest.Value;
        // Accept SQLite file: URIs the same way the CLI does (QueryCommandRunner.WithDb),
        // so AI agents on read-only mounts can pass `--db file:///abs/path?immutable=1` and
        // reach the read-only escape hatch in DbContext. File.Exists is skipped for URI-
        // shaped values because they may carry query params meaningless to the filesystem.
        // CLI と同じく file: URI を受け付け、サンドボックス用の escape hatch に到達できるようにする。
        var isUri = _dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!isUri && !File.Exists(LongPath.EnsureWindowsPrefix(_dbPath)))
        {
            // Drop any stale cached context so the next tool call can re-open after the user
            // creates the DB (e.g. via an external `cdidx index`). Without this, a missed
            // file lookup would leave a closed/disposed handle blocking later open attempts.
            // ユーザーが後から DB を作った場合に再オープンできるよう、キャッシュをここで破棄。
            if (!isolateRequestDb)
                CloseSharedDb();
            return CreateToolErrorResponse(true, id, $"Database not found: {_dbPath}. Run 'cdidx index <projectPath>' first.",
                category: McpErrorEnvelope.CategoryIndexMissing,
                suggestion: "Run `cdidx index <projectPath>` to build the index before retrying. The DB lives at `.cdidx/codeindex.db` by default.",
                retrySafe: true);
        }

        var requestToken = _currentRequestToken.Value;
        requestToken.ThrowIfCancellationRequested();
        if (isolateRequestDb)
        {
            using var isolatedDb = new DbContext(DbOpenIntent.QueryOnly, _dbPath, requestToken);
            using var isolatedReader = new DbReader(isolatedDb, requestToken);
            isolatedReader.IncludeGenerated = args?["includeGenerated"]?.GetValue<bool>() ?? false;
            return RunWithSqliteDiagnostics(isolatedReader, action);
        }

        // Artifact-preserving WAL reads use detached private snapshots. Refresh them between
        // MCP calls when the source generation changes so a long-lived server
        // observes commits made after the previous call while each individual call keeps one
        // stable SQLite snapshot.
        // artifact-preserving WAL read は切り離した private snapshot を使う。各呼び出し内の
        // 一貫性を保ちつつ、長時間動作する MCP が source generation の変更後に新しい
        // commit を観測できるよう、呼び出し間でそれらの handle を更新する。
        if (_sharedDb?.OpenIntent == DbOpenIntent.QueryOnly
            && _sharedDb.QueryOnlySnapshotRequiresRefresh
            && !_sharedDb.IsQueryOnlySnapshotCurrent(requestToken))
        {
            CloseSharedDb();
        }

        var db = GetOrOpenSharedDb(DbOpenIntent.QueryOnly);
        // Reuse the connection-scoped schema cache for single-threaded direct callers so each
        // call no longer re-runs PRAGMA table_info / PRAGMA index_list per DbReader (issue #1565),
        // and hand the per-request cancellation token to the reader so SQLite work
        // the tool kicks off can observe shutdown / client-disconnect cancellation
        // (#1567). The token is `CancellationToken.None` outside an in-flight request,
        // preserving the existing behaviour for ad-hoc callers like tests that drive
        // `WithDbReader` through internals.
        // MCP ツール呼び出しごとの schema 再走査を排除し (issue #1565)、
        // per-request cancellation token を reader に渡して SQLite 作業が
        // shutdown / 切断を観測できるようにする (#1567)。
        using var reader = new DbReader(db, requestToken);
        reader.IncludeGenerated = args?["includeGenerated"]?.GetValue<bool>() ?? false;
        return RunWithSqliteDiagnostics(reader, action);
    }

    private JsonNode RunWithSqliteDiagnostics(DbReader reader, Func<DbReader, JsonNode> action)
    {
        var previousReader = _activeSqliteDiagnosticsReader.Value;
        _activeSqliteDiagnosticsReader.Value = reader;
        try
        {
            return reader.RunWithGeneratedScope(() => action(reader));
        }
        finally
        {
            _activeSqliteDiagnosticsReader.Value = previousReader;
        }
    }

    private void AddConfiguredSqliteDiagnostics(JsonObject payload)
    {
        var diagnosticsReader = _activeSqliteDiagnosticsReader.Value;
        if (diagnosticsReader != null)
        {
            QueryCommandRunner.AddReadOnlyFallbackDiagnostics(payload, diagnosticsReader);
            return;
        }

        if (!SqliteFileUri.RequestsImmutableSnapshot(_dbPath))
            return;

        payload["wal_stale_snapshot_risk"] = true;
        payload["wal_stale_snapshot_reason"] = "explicit_immutable_read_only";
    }

    /// <summary>
    /// Open the per-session DbContext on first use and reuse it while the requested intent matches.
    /// Centralising the open lets us pay the connection setup, pragma application, and SQL
    /// function registration once per direct session instead of once per tool invocation
    /// (#1494). Transport requests that may time out independently use isolated DB contexts.
    /// 直接呼び出しセッション初回に DbContext を開き、以後は再利用する。timeout 後も独立して
    /// 継続し得る transport リクエストは、共有接続を避けるためリクエスト単位の DB context を使う。
    /// </summary>
    internal DbContext GetOrOpenSharedDb(DbOpenIntent openIntent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sharedDb?.OpenIntent == openIntent)
            return _sharedDb;

        CloseSharedDb();
        _sharedDb = new DbContext(openIntent, _dbPath, _currentRequestToken.Value);
        return _sharedDb;
    }

    private void CloseSharedDb()
    {
        _sharedDb?.Dispose();
        _sharedDb = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseSharedDb();
        var shutdownCancellationTask = RequestShutdownCancellation();
        if (shutdownCancellationTask.IsCompleted)
        {
            CompleteShutdownCleanup();
        }
        else
        {
            _ = shutdownCancellationTask.ContinueWith(
                static (_, state) => ((McpServer)state!).CompleteShutdownCleanup(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        // Bounded transport teardown can intentionally leave a late task that still releases
        // this gate. As with `_sharedDbWriteGate`, keep the managed semaphore undisposed so
        // eventual completion cannot fail with ObjectDisposedException (#3999, #4543).
        // bounded transport teardown 後も late task がこの gate を release し得るため、
        // `_sharedDbWriteGate` と同様に dispose せず、遅延完了時の例外を防ぐ (#3999, #4543)。
        _textWriterGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void CompleteShutdownCleanup()
    {
        lock (s_serverLifecycleGate)
        {
            s_activeServerCount--;
            if (s_activeServerCount == 0)
                ExtractorPluginRegistry.ReleaseWorkspaceSnapshots();
        }
        DisposeShutdownCtsOnce();
    }

    internal static int ActiveServerCountForTests()
    {
        lock (s_serverLifecycleGate)
            return s_activeServerCount;
    }

    private void DisposeShutdownCtsOnce()
    {
        if (Interlocked.Exchange(ref _shutdownCtsDisposed, 1) == 0)
            _shutdownCts.Dispose();
    }

}
