# Synchronization Model

This note is an implementation-facing inventory for issue #4154. It documents
who owns the main synchronization primitives so future reviews can distinguish
intended serialization from accidental contention.

## Semaphore Gates

| Area | Primitive | Owner and contract | Current coverage |
|---|---|---|---|
| `DbWriter` | `_transactionGate`, `_transactionStateLock`, `_currentTransactionGateToken` | A single `DbWriter` owns transaction state for its SQLite connection. The outermost `TransactionScope` holds `_transactionGate` until dispose. Same-stack nested calls from the owning thread and `AsyncLocal` token skip the semaphore and use `SAVEPOINT`; any other flow waits for depth to return to zero, even if `ExecutionContext` copied the token into `Task.Run`. `_transactionStateLock` protects depth, owner diagnostics, and wait notifications. | `DatabaseTests.BeginTransaction_SameOwnerNestedScopeUsesSavepointRollback_Issue4154`, cancellation and timeout diagnostics, commit/dispose race coverage. |
| `McpServer` | `_concurrencyGate` | The server owns the gate as the effective upper bound for concurrently executing stdio and request-context-aware transport requests. Base loops do not acquire an outer permit, so a request at `maxConcurrency: 1` takes exactly one slot at dispatch. Request ids register before gate/barrier waits, execution timeout begins after acquisition, and a timed-out cancellation-insensitive action retains its lease until true completion. Cancellation controls bypass the gate so they remain readable while all execution slots are occupied. The managed gate remains undisposed because bounded transport teardown may leave a late releaser. | Signal-driven stdio/HTTP/base overlap, cap, ordering, queued-cancellation, timeout-lease, and teardown tests. |
| `McpServer` | local `admissionGate`, `_activeRequests` | Each concurrent frame loop separately bounds accepted ordinary frames at `MaxConcurrency + 64` by default. Admission is non-blocking so cancellation/client-response controls remain readable; overflow requests receive retry-safe `-32003` / `server_busy`. Accepted request ids enter `_activeRequests` before protocol or execution waits, so queued cancellation does not depend on the 64-entry short-lived pre-registration tombstone cache. | Admission overflow and more-than-64 queued-cancellation tests. |
| `McpServer` | `_sessionStateGate`, protocol barrier tasks | The lock publishes coherent client identity, capabilities, roots, log-level, and audit snapshots. Initialize and other session-mutating frames form receive-order barriers: they wait for previously accepted work, while later normal requests wait for the barrier without blocking cancellation or client-response frames. | Initialization-order, session-state, and concurrent request tests. |
| `McpServer` | `_shutdownCancellationGate`, `_shutdownCancellationTask` | Shutdown cancellation starts once through `CancellationTokenSource.CancelAsync`. The stored task observes callback failures and participates in the post-cancel deadline; drain re-reads it after accepted work completes so callbacks started after the initial snapshot are still bounded. The token source is disposed only after callback completion. | Blocking/throwing/late shutdown-callback and bounded-drain tests. |
| `McpServer` | `_sharedDbWriteGate` | MCP transport reads use request-isolated DB contexts. The server serializes `index` and `backfill_fold` before either touches the shared session writer context. The gate remains undisposed because bounded teardown may leave a late owner that must release it safely. | MCP writer, cancellation, and concurrent request tests. |
| `McpServer` | `_textWriterGate` | The server serializes direct `TextWriter` writes for line-oriented protocol input so diagnostic output does not interleave with concurrent request handling. | MCP protocol and console-output tests. |
| `McpServer` | local `writeGate` | One frame loop owns this gate and serializes protocol writes only. Normal request execution is independent and bounded by `_concurrencyGate`; request-scoped HTTP response writers keep out-of-order completion attached to the originating POST. Every loop exit, including external cancellation while an inline control or overload write waits, reaches one grace/cancel/post-cancel drain. Terminal response/completion writes join the same deadline. Gate disposal is deferred until every accepted task and terminal write completes. | MCP frame-loop write, response-binding, ordering, blocked-writer/gate, and signal-driven teardown tests. |
| `StdioMcpTransport` | `_disposeGate`, `_disposeBarrier` | Disposal closes input promptly to unblock reads. Output remains writable until the server-published aggregate of accepted tasks and terminal writes completes, then the writer and stdout are disposed even if input disposal failed. | Late-writer barrier and input-dispose-failure tests. |
| `HttpMcpTransport` | `_queueSlots` | The transport owns queue capacity. HTTP handlers acquire a slot before `TryWrite`; `ReadFrameAsync` releases the slot after dequeuing. Full queues reject requests instead of blocking handlers indefinitely. | HTTP MCP queue-limit tests. |
| `HttpMcpTransport` | `_handlerSemaphore` | The transport owns the concurrent HTTP handler cap. The accept loop uses non-blocking admission and rejects over-limit handlers. Disposal waits for owned semaphore gates to go idle before disposing them. | HTTP MCP concurrent-handler and dispose tests. |
| `HttpMcpTransport.EventStream` | `_writeGate` | Each SSE stream owns its own write gate so heartbeats and JSON-RPC events cannot interleave bytes on the same response stream. Write timeouts abort only that stream. | HTTP MCP event-stream timeout and drop tests. |

## Lock and Atomic State Hotspots

The latest dogfood pass found the highest `lock (` counts in symbol extraction,
suggestion persistence, console output, dependency package extraction, schema
caching, plugin registry loading, `DbWriter`, and MCP request handling. The
highest atomic-state counts are in `HttpMcpTransport`, `McpServer`, `DbWriter`,
`AuditLogSink`, and `DbDebug`.

Use this inventory as the starting point before changing those areas:

- Identify the owning object and lifetime of each primitive.
- Keep queue/backpressure gates non-blocking at HTTP or MCP admission points.
- Keep SQLite writer transaction ownership tied to `TransactionScope` dispose.
- Add deterministic race or timeout tests when changing shutdown, disposal,
  cancellation, queue overflow, or nested transaction behavior.

## 日本語

このメモは issue #4154 のための実装向け inventory です。主な同期 primitive
について、所有者と直列化の意図を明示し、今後の review で偶発的な競合と区別できる
ようにします。

### Semaphore gate

| 領域 | primitive | 所有者と契約 | 現在の coverage |
|---|---|---|---|
| `DbWriter` | `_transactionGate`, `_transactionStateLock`, `_currentTransactionGateToken` | 1 つの `DbWriter` が SQLite connection の transaction state を所有します。最外 `TransactionScope` は dispose まで `_transactionGate` を保持します。同じ thread と `AsyncLocal` token を持つ同一 call stack の nested call だけ semaphore を再取得せず `SAVEPOINT` になります。それ以外の flow は、`Task.Run` に `ExecutionContext` が token をコピーしていても depth が 0 に戻るまで待機します。`_transactionStateLock` は depth、owner diagnostics、wait notification を保護します。 | `DatabaseTests.BeginTransaction_SameOwnerNestedScopeUsesSavepointRollback_Issue4154`、cancellation/timeout diagnostics、commit/dispose race coverage。 |
| `McpServer` | `_concurrencyGate` | server が stdio と request-context 対応 transport request の実効的な同時実行上限として所有します。base loop は outer permit を取得しないため、`maxConcurrency: 1` の request は dispatch 時に slot を1つだけ取得します。request id は gate/barrier 待機前に登録し、execution timeout は取得後に開始し、timeout 後も cancellation を無視する action は実完了まで lease を保持します。全 execution slot 使用中も cancellation control は gate を bypass して読み続けられます。bounded transport teardown 後に late releaser が残り得るため managed gate は dispose しません。 | signal-driven な stdio/HTTP/base overlap、上限制御、順序、queued cancellation、timeout lease、teardown tests。 |
| `McpServer` | local `admissionGate`、`_activeRequests` | concurrent frame loop ごとに通常 accepted frame を既定で `MaxConcurrency + 64` に制限します。admission は non-blocking のため cancellation/client-response control を読み続け、超過 request には retry-safe な `-32003` / `server_busy` を返します。accepted request id は protocol/execution 待機前に `_activeRequests` へ入るため、queued cancellation は 64 件の短命な登録前 tombstone cache に依存しません。 | admission overflow と 64 件超 queued-cancellation tests。 |
| `McpServer` | `_sessionStateGate`、protocol barrier task | lock は client identity、capabilities、roots、log level、audit snapshot を一貫して publish します。initialize など session を変更する frame は受信順 barrier となり、先に受理した処理を待ち、後続通常 request は barrier を待ちます。cancellation/client-response frame は block しません。 | initialization 順序、session state、並行 request tests。 |
| `McpServer` | `_shutdownCancellationGate`、`_shutdownCancellationTask` | shutdown cancellation は `CancellationTokenSource.CancelAsync` で一度だけ開始します。保存 task が callback failure を観測して post-cancel deadline に参加し、accepted work の drain 後にも再取得するため、初回 snapshot 後に開始した callback も bounded です。token source は callback 完了後だけ dispose します。 | 停止/例外/late shutdown-callback と bounded-drain tests。 |
| `McpServer` | `_sharedDbWriteGate` | MCP transport の read は request 単位の独立 DB context を使います。`index` と `backfill_fold` は shared session writer context に触る前に server 内で直列化します。bounded teardown 後の late owner が安全に release できるよう gate は dispose しません。 | MCP writer、cancellation、並行 request tests。 |
| `McpServer` | `_textWriterGate` | line-oriented protocol input で `TextWriter` への直接書き込みを直列化し、diagnostic output が concurrent request handling と混ざらないようにします。 | MCP protocol と console-output tests。 |
| `McpServer` | local `writeGate` | 1 つの frame loop が所有し、protocol write だけを直列化します。通常 request 実行は独立し `_concurrencyGate` で上限化します。request-scoped な HTTP response writer により完了順が前後しても元の POST に応答します。inline control / overload write 待機中の external cancellation を含む全 loop 終了経路が共通の grace/cancel/post-cancel drain を通り、terminal response/completion write も同じ deadline に参加します。gate は全 accepted task と terminal write の完了後に dispose します。 | MCP frame-loop write、response binding、ordering、blocked writer/gate、signal-driven teardown tests。 |
| `StdioMcpTransport` | `_disposeGate`、`_disposeBarrier` | read を unblock するため input は速やかに dispose します。server が公開した accepted task / terminal write の aggregate 完了まで output は書き込み可能なまま保ち、その後 input dispose が失敗していても writer と stdout を dispose します。 | late-writer barrier と input-dispose-failure tests。 |
| `HttpMcpTransport` | `_queueSlots` | transport が queue capacity を所有します。HTTP handler は `TryWrite` 前に slot を取得し、`ReadFrameAsync` が dequeue 後に slot を返します。queue full 時は handler を無期限 block せず request を拒否します。 | HTTP MCP queue-limit tests。 |
| `HttpMcpTransport` | `_handlerSemaphore` | transport が concurrent HTTP handler の上限を所有します。accept loop は non-blocking admission を使い、上限超過 handler を拒否します。dispose は所有 semaphore gate が idle になるまで待ってから破棄します。 | HTTP MCP concurrent-handler と dispose tests。 |
| `HttpMcpTransport.EventStream` | `_writeGate` | SSE stream ごとに write gate を所有し、heartbeat と JSON-RPC event の byte stream が同じ response 上で interleave しないようにします。write timeout はその stream だけを abort します。 | HTTP MCP event-stream timeout/drop tests。 |

### lock と atomic state の hotspot

最新の dogfood pass では、`lock (` は symbol extraction、suggestion persistence、
console output、dependency package extraction、schema cache、plugin registry、
`DbWriter`、MCP request handling に集中していました。atomic state は
`HttpMcpTransport`、`McpServer`、`DbWriter`、`AuditLogSink`、`DbDebug` に集中して
います。

これらの領域を変更するときは、まず次を確認します。

- 各 primitive の所有 object と lifetime を特定する。
- HTTP/MCP の admission point では queue/backpressure gate を non-blocking に保つ。
- SQLite writer transaction ownership は `TransactionScope` dispose に結び付ける。
- shutdown、disposal、cancellation、queue overflow、nested transaction を変える場合は
  deterministic race test または timeout test を追加する。
