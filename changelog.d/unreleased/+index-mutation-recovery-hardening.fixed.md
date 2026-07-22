---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Database/DbWriter.FilePurge.cs
  - src/CodeIndex/Database/DbWriter.Fts.cs
  - src/CodeIndex/Database/DbWriter.Transactions.cs
  - src/CodeIndex/Database/FtsBulkLoadTriggerGuard.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerUpdateTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Index mutation recovery now preserves committed and reappearing state across failure boundaries** — Bulk FTS guards observe durable writer commits before throwable post-commit bookkeeping and WAL checkpoints, so MCP cleanup rebuilds searchable state before clearing its recovery marker even when the caller mutation flag was not yet set. Transaction finalization keeps the writer gate until post-commit actions have completed or failed, and rollback detaches its completed transaction before exposing a terminal state to concurrent disposal. The primary process marker remains legacy-readable `pid:<pid>`, while a separately associated PID-bound start-time generation or per-process token detects current-PID reuse; persistent cleanup triggers prevent an older writer from leaving that generation stale, and unverifiable ownership remains conservatively active. File purges now honor cancellation while waiting for the transaction gate, and expanded C# cleanup guards compare normalized repository `IndexPath` values so an exact reappearing row is preserved until a clean retry. CLI and MCP index responses now retain discovered C# and SQL presence across pre-write aborts and per-file failures, including fresh, rebuild, and partial discovery, so readiness no longer depends on which file was persisted first. MCP also demotes the durable SQL graph contract before mutation and only restamps it after clean completion, keeping both the immediate response and subsequent readers degraded after a partial run.

## 日本語

- **index mutation recovery が failure 境界をまたいで commit 済み・再出現 state を保持するようになりました** — bulk FTS guard は例外を投げ得る post-commit bookkeeping / WAL checkpoint より前の durable writer commit を監視するため、caller の mutation flag が未設定でも MCP cleanup は recovery marker を消す前に検索可能 state を rebuild します。transaction finalization は post-commit action の完了または失敗確定まで writer gate を保持し、rollback は並行 Dispose に terminal state を見せる前に完了済み transaction を detach します。primary process marker は旧 reader が読める `pid:<pid>` を維持し、別に関連付けた PID 付き start-time generation または process ごとの token が current PID の再利用を検出します。永続 cleanup trigger は旧 writer による stale generation の残留を防ぎ、確認不能な owner は安全側で active と扱います。file purge は transaction gate 待機中の cancellation を監視し、expanded C# cleanup guard は repository の正規化済み `IndexPath` を比較して、exact に再出現した row を clean retry まで保持します。CLI / MCP の index response は write 前 abort と file 単位 failure の双方で発見済み C# / SQL presence を保持し、fresh / rebuild / partial discovery のいずれでも、どの file が先に永続化されたかによって readiness が変わりません。さらに MCP は mutation 前に durable SQL graph contract を降格し、clean completion 後だけ再 stamp するため、partial run 後は直後の response と後続 reader の双方が degraded のままになります。
