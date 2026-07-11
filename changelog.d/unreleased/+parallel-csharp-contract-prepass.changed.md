---
category: changed
affected:
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
---

## English

- **Large C# workspaces probe static-interface contracts in parallel** - CLI full scans and file-scoped updates now apply configured index parallelism to the read/filter/extract portion of the C# static-interface prepass. Database reuse decisions remain serialized, results are merged in deterministic file order, and MCP retains its documented serial indexing behavior.

## 日本語

- **巨大な C# workspace の static-interface contract probe を並列化しました** - CLI full scan と file-scoped update は、C# static-interface prepass の read / filter / extract 部分に設定済み index parallelism を適用します。database reuse 判定は直列のまま維持し、結果は決定的な file 順で統合し、MCP は文書化済みの serial indexing 動作を維持します。
