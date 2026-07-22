---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ChunkSymbolBatches.cs
  - src/CodeIndex/Database/DbWriter.FileCleanup.cs
  - src/CodeIndex/Database/DbWriter.FilePurge.cs
  - src/CodeIndex/Database/DbWriter.Files.cs
  - src/CodeIndex/Database/DbWriter.TypeScriptAugmentations.cs
  - src/CodeIndex/Database/DbWriter.UnsupportedReferences.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerUpdateTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - TESTING_GUIDE.md
---

## English

- **Incremental reference-graph finalization now follows only committed dirty identities** — full-scan, scoped-update, and MCP indexing retain old/new symbol names and reciprocal edges across deletion, rename, and language transitions, then rebuild candidates only for affected references. Every scoped update and candidate phase is driven by dirty reference primary-key seeks, C# instantiate grouping starts from dirty lookup names, and tiny production scopes skip the whole-table reference count. Transaction rollbacks do not leak dirty state, cancellation leaves the readiness contract degraded for a safe retry, and fresh/rebuild, missing-contract, or broad changes still use the full refresh. On a controlled 324,000-reference repository snapshot, a one-reference refresh fell from a 1,567.433 ms median to 90.547 ms (17.31× faster) while matching a subsequent full refresh.

## 日本語

- **incremental reference graphのfinalizeがcommit済みdirty identityだけを追跡するようになりました** — full scan、scoped update、MCP indexingは削除・rename・言語遷移をまたいで旧・新symbol名と逆辺を保持し、影響を受けるreferenceだけのcandidateを再構築します。scoped updateとcandidateの全phaseはdirty referenceの主キーseekを起点とし、C# instantiate groupingはdirty lookup nameから開始し、本番の小さなscopeではreference table全件COUNTも省略します。transaction rollbackのdirty状態は残らず、cancel時は安全なretryのためreadiness契約をdegradedのまま維持し、fresh/rebuild・契約欠落・広範な変更では従来のfull refreshへ戻ります。324,000 referenceの制御repository snapshotでは、1 referenceのrefresh中央値が1,567.433 msから90.547 msへ短縮され（17.31倍高速）、後続full refreshとの一致も確認しました。
