# Synchronization Model

This note is an implementation-facing inventory for issue #4154. It documents
who owns the main synchronization primitives so future reviews can distinguish
intended serialization from accidental contention.

## Semaphore Gates

| Area | Primitive | Owner and contract | Current coverage |
|---|---|---|---|
| `DbWriter` | `_transactionGate`, `_transactionStateLock`, `_currentTransactionGateToken` | A single `DbWriter` owns transaction state for its SQLite connection. The outermost `TransactionScope` holds `_transactionGate` until dispose. Same-stack nested calls from the owning thread and `AsyncLocal` token skip the semaphore and use `SAVEPOINT`; any other flow waits for depth to return to zero, even if `ExecutionContext` copied the token into `Task.Run`. `_transactionStateLock` protects depth, owner diagnostics, and wait notifications. | `DatabaseTests.BeginTransaction_SameOwnerNestedScopeUsesSavepointRollback_Issue4154`, cancellation and timeout diagnostics, commit/dispose race coverage. |
| `McpServer` | `_concurrencyGate` | The server owns the gate as the upper bound for in-flight tool calls. The read loop must still accept cancellation and response frames while normal requests run, so each dispatched request releases the gate in its task `finally` block. | MCP concurrency, cancellation, and timeout tests. |
| `McpServer` | `_textWriterGate` | The server serializes direct `TextWriter` writes for line-oriented protocol input so diagnostic output does not interleave with concurrent request handling. | MCP protocol and console-output tests. |
| `McpServer` | local `writeGate`, `normalFrameGate` | One frame loop owns these gates. `writeGate` serializes transport writes; `normalFrameGate` preserves normal request frame ordering while allowing cancellation and response frames to bypass that lane. Late request tasks may outlive EOF drain, so these gates are disposed only when all tasks have completed. | MCP frame-loop ordering and drain tests. |
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
| `McpServer` | `_concurrencyGate` | server が in-flight tool call の上限として所有します。通常 request の実行中も read loop は cancellation/response frame を受ける必要があるため、dispatch された request task の `finally` で gate を解放します。 | MCP concurrency、cancellation、timeout tests。 |
| `McpServer` | `_textWriterGate` | line-oriented protocol input で `TextWriter` への直接書き込みを直列化し、diagnostic output が concurrent request handling と混ざらないようにします。 | MCP protocol と console-output tests。 |
| `McpServer` | local `writeGate`, `normalFrameGate` | 1 つの frame loop が所有します。`writeGate` は transport write を直列化し、`normalFrameGate` は通常 request frame の順序を保ちながら cancellation/response frame を別 lane で処理できるようにします。EOF drain 後も late request task が残ることがあるため、全 task 完了時だけ dispose します。 | MCP frame-loop ordering と drain tests。 |
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
