---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.TypeScriptAugmentations.cs
  - src/CodeIndex/Database/DbWriter.Transactions.cs
  - src/CodeIndex/Database/DbWriter.Files.cs
  - src/CodeIndex/Database/DbWriter.ChunkSymbolBatches.cs
  - src/CodeIndex/Database/DbWriter.FilePurge.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerUpdateTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - tests/CodeIndex.Tests/TestProjectHelper.cs
  - TESTING_GUIDE.md
---

## English

- **Incremental TypeScript augmentation refreshes are scoped to changed interface names** — full scans, `--files` updates, and MCP indexing now track old and new interface names, including stale-file deletion and persisted TypeScript-to-other-language transitions, then delete and rebuild only augmentation rows sharing those names in bounded batches while preserving file module classification. Fresh/rebuild runs, broad dirty sets, contract or project-root changes, and upfront forced extractor refreshes retain the authoritative full fallback; runtime JavaScript/TypeScript configuration refreshes track every refreshed file and use that same adaptive fallback. In a controlled 50,002-declaration augmentation rebuild with one dirty merged name, median elapsed time fell by about 44% and current-thread allocation by more than 99%.

## 日本語

- **incremental TypeScript augmentation refreshを変更interface名に限定しました** — full scan、`--files` update、MCP indexingはstale-file削除と永続化済みTypeScript→他言語遷移も含めて変更前後のinterface名を追跡し、fileのmodule分類を維持しながら、その名前を共有するaugmentation行だけをbounded batchで削除・再構築します。fresh/rebuild、広範なdirty集合、contract・project root変更、開始時点での強制extractor refreshではauthoritativeな全量fallbackを維持し、実行中のJavaScript/TypeScript設定refreshは全refresh対象fileを追跡して同じadaptive fallbackを使います。50,002宣言中1個のdirty merged nameを対象にしたaugmentation rebuildの制御benchmarkでは、median elapsed timeを約44%、current-thread allocationを99%以上削減しました。
