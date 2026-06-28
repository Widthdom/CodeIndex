# .NET Sync/Cancellation Risk Audit (#4059)

> [日本語版はこちら / Japanese version](#net-synccancellation-risk-audit-4059-日本語)

This note classifies the production `dotnet-risk-patterns/cancellation-token-none`
and `dotnet-risk-patterns/sync-over-async` hits reviewed for #4059.

## English

### Remediated hits

| Area | Prior hit | Classification | Action |
|---|---|---|---|
| `DiffCommandRunner` | Private `DiffOrderedRows` and `RowsEqual` overloads without a token | Fix-needed convenience wrappers | Removed. Production callers already had a caller token. |
| `ExportImportCommandRunner` | Internal no-token helpers for archive writes, table counts, database snapshots, and import replacement | Fix-needed/test convenience wrappers | Removed. Tests now call token-aware helpers with an explicit `CancellationToken.None`. |

### Retained `CancellationToken.None` hits

| Area | Classification | Rationale |
|---|---|---|
| `DbWriter` transaction and batch-write convenience overloads | Intentional compatibility API | Token-aware overloads exist and indexing paths use them; the no-token overloads preserve existing direct callers. |
| `DbContext` checkpoint and vacuum convenience overloads | Intentional compatibility API | Token-aware overloads exist for command paths; the no-token overloads remain direct-call conveniences. |
| `DbCommandRunner.Run`, `DiffCommandRunner.Run`, and `QueryCommandRunner.RunVacuum` | CLI compatibility boundary | `ProgramRunner` dispatches with its cancellation token; the no-token overloads remain for direct callers/tests. |
| `LspServer.Run` and `LspServer.TryReadMessage` | Legacy synchronous wrapper | Async/token-aware entrypoints are the preferred transport path and are documented next to the wrappers. |
| `McpServer` request-token resets and late continuations | Detached cleanup/observer boundary | These continuations must still run after request cancellation to release gates, dispose linked tokens, or observe late faults. |
| `HttpMcpTransport` late output observer and response fallback | Detached observer or no-request fallback | Abandoned output writes need an uncancelled fault observer; fallback responses have no `PendingRequest` token. |
| `IndexCommandRunner.FullScan` and `IndexCommandRunner.UpdateTargets` continuations | Detached cleanup boundary | Completion/heartbeat cleanup must run even when the caller token has been canceled. |
| `GitHelper.WaitForGitExitAsync` timeout delay | Timeout sentinel | The timeout task must not be canceled by caller cancellation because cancellation is represented by a separate task. |
| `IssueDuplicatePreflight.TryLoad` | Legacy synchronous wrapper | Async/token-aware loading is used by command paths that have caller cancellation. |
| `SuggestionStore.TryAddAndSubmit` and no-token async convenience overload | Compatibility API | MCP uses the token-aware async overload; the existing sync/direct async overloads remain for direct callers. |
| `DbReader` legacy constructor | Compatibility API | MCP and other request-scoped paths use the token-aware constructor; the old constructor preserves direct callers. |
| `AuditLogSink.WaitForIdle` and `BackgroundTaskObserver.Run(Func<Task>, ...)` | Convenience overload | Token-aware overloads exist for callers that own a meaningful cancellation token. |

### Retained `sync-over-async` hits

| Area | Classification | Rationale |
|---|---|---|
| `ProgramRunner` MCP and HTTP MCP launch waits | Process/CLI boundary | The top-level command runner is synchronous; async server loops still observe signal/request cancellation internally. |
| `ProgramRunner.RunInstallerProcessDetailed` | Process/CLI boundary | The method is bounded by timeout/cancellation and kills the subprocess on cancellation or timeout. |
| `McpServer.ProcessFrame` and `McpServer.HandleMessage` | Legacy synchronous wrapper | Transports use async entrypoints; wrappers remain for legacy in-process callers and tests. |
| `McpServer.ObserveCompletedRequestTask` | Completed-task fault observer | It only observes an already-faulted task so the exception is logged without changing shutdown behavior. |
| `LspServer.Run` and `LspServer.TryReadMessage` | Legacy synchronous wrapper | Async/token-aware APIs are available for transports with shutdown tokens. |
| `GitHelper.RunProcessCapturingResult` | Synchronous git helper boundary | Callers propagate cancellation; converting the helper graph to async is broader than #4059 and not required for shutdown correctness. |
| `SymbolExtractionWorker` and `PostExtractionHookCallbackWorker` | Isolated worker protocol boundary | `WaitForTask` proves send/read tasks completed, timed out, or observed cancellation before `GetResult` reads the completed result. |
| `ConsoleUi.StopSpinner` | Synchronous teardown boundary | Spinner shutdown is best-effort during a synchronous dispose-style path; faults are intentionally swallowed after observation. |
| `IssueDuplicatePreflight.TryLoad` and `SuggestionStore.TryAddAndSubmit` | Legacy synchronous wrapper | Command/MCP paths with caller cancellation use async/token-aware APIs. |

After the remediation above, no remaining #4059 audit hit is classified as fix-needed.

<a id="net-synccancellation-risk-audit-4059-日本語"></a>
## .NET sync/cancellation risk audit (#4059) 日本語

このメモは #4059 で確認した production の
`dotnet-risk-patterns/cancellation-token-none` と
`dotnet-risk-patterns/sync-over-async` のヒットを分類する。

### 対応したヒット

| 領域 | 以前のヒット | 分類 | 対応 |
|---|---|---|---|
| `DiffCommandRunner` | token を受け取らない private `DiffOrderedRows` / `RowsEqual` overload | 修正対象の convenience wrapper | 削除。production 呼び出し側は既に caller token を持っていた。 |
| `ExportImportCommandRunner` | archive 書き込み、table count、DB snapshot、import replacement の internal no-token helper | 修正対象 / test convenience wrapper | 削除。テストは token-aware helper に明示的な `CancellationToken.None` を渡す。 |

### 残す `CancellationToken.None` ヒット

| 領域 | 分類 | 理由 |
|---|---|---|
| `DbWriter` の transaction / batch write convenience overload | 意図的な互換 API | token-aware overload は存在し、indexing 経路はそちらを使う。no-token overload は既存 direct caller の互換用に残す。 |
| `DbContext` の checkpoint / vacuum convenience overload | 意図的な互換 API | command 経路用の token-aware overload は存在する。no-token overload は direct call 用の便宜 API。 |
| `DbCommandRunner.Run`、`DiffCommandRunner.Run`、`QueryCommandRunner.RunVacuum` | CLI 互換境界 | `ProgramRunner` dispatch は cancellation token を渡す。no-token overload は direct caller / test 用に残す。 |
| `LspServer.Run` と `LspServer.TryReadMessage` | legacy synchronous wrapper | transport では async / token-aware entrypoint を優先し、wrapper 近傍にもその前提を記載済み。 |
| `McpServer` の request-token reset と late continuation | detached cleanup / observer 境界 | request cancellation 後でも gate 解放、linked token dispose、late fault 観測を実行する必要がある。 |
| `HttpMcpTransport` の late output observer と response fallback | detached observer または no-request fallback | 放棄された出力書き込みには未キャンセルの fault observer が必要で、fallback response には `PendingRequest` token がない。 |
| `IndexCommandRunner.FullScan` と `IndexCommandRunner.UpdateTargets` の continuation | detached cleanup 境界 | caller token がキャンセル済みでも completion / heartbeat cleanup は実行する必要がある。 |
| `GitHelper.WaitForGitExitAsync` の timeout delay | timeout sentinel | timeout task は caller cancellation でキャンセルしてはいけない。キャンセルは別 task で表す。 |
| `IssueDuplicatePreflight.TryLoad` | legacy synchronous wrapper | caller cancellation を持つ command 経路は async / token-aware loading を使う。 |
| `SuggestionStore.TryAddAndSubmit` と no-token async convenience overload | 互換 API | MCP は token-aware async overload を使う。既存の sync / direct async overload は direct caller 用に残す。 |
| `DbReader` の legacy constructor | 互換 API | MCP など request-scoped 経路は token-aware constructor を使う。旧 constructor は direct caller 互換用。 |
| `AuditLogSink.WaitForIdle` と `BackgroundTaskObserver.Run(Func<Task>, ...)` | convenience overload | 意味のある cancellation token を持つ caller 向けの token-aware overload が存在する。 |

### 残す `sync-over-async` ヒット

| 領域 | 分類 | 理由 |
|---|---|---|
| `ProgramRunner` の MCP / HTTP MCP 起動待ち | process / CLI 境界 | top-level command runner は同期 API。async server loop 自体は signal / request cancellation を内部で観測する。 |
| `ProgramRunner.RunInstallerProcessDetailed` | process / CLI 境界 | timeout / cancellation で境界があり、キャンセルまたは timeout 時には subprocess を kill する。 |
| `McpServer.ProcessFrame` と `McpServer.HandleMessage` | legacy synchronous wrapper | transport は async entrypoint を使う。wrapper は legacy in-process caller と test 用。 |
| `McpServer.ObserveCompletedRequestTask` | completed-task fault observer | 既に fault した task を観測してログへ流すだけで、shutdown 挙動は変えない。 |
| `LspServer.Run` と `LspServer.TryReadMessage` | legacy synchronous wrapper | shutdown token を持つ transport 向けに async / token-aware API がある。 |
| `GitHelper.RunProcessCapturingResult` | 同期 git helper 境界 | caller は cancellation を伝搬する。helper graph 全体の async 化は #4059 より広く、shutdown correctness には不要。 |
| `SymbolExtractionWorker` と `PostExtractionHookCallbackWorker` | isolated worker protocol 境界 | `WaitForTask` が send/read task の完了、timeout、または cancellation 観測を確認してから、完了済み結果を `GetResult` で読む。 |
| `ConsoleUi.StopSpinner` | 同期 teardown 境界 | spinner shutdown は同期 dispose 風の経路で best-effort。fault は観測後に意図的に握りつぶす。 |
| `IssueDuplicatePreflight.TryLoad` と `SuggestionStore.TryAddAndSubmit` | legacy synchronous wrapper | caller cancellation を持つ command / MCP 経路は async / token-aware API を使う。 |

上記対応後、#4059 の残存 audit hit に修正対象として分類されるものはない。
