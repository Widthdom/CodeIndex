---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.Readiness.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.Readiness.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.Execution.cs
  - src/CodeIndex/Database/DbWriter.TypeScriptAugmentations.cs
---

## English

- **Large TypeScript indexes avoid refreshing the reference graph twice** — CLI full scans, scoped updates, and MCP indexing now let a planned declaration-augmentation rebuild perform the single graph-finalization pass while keeping candidate-index staging active through that pass. Empty results still finalize deletions or an inherited graph pass, marker-only validation skips a whole-graph scan, late validation failures fall back before partial readiness, and `--memory-trace` attributes the graph sample after the augmentation work. Authoritative scans without TypeScript stamp the contract without rebuilding augmentation rows.

## 日本語

- **大規模 TypeScript index で reference graph を2回 refresh しないようになりました** — CLI full scan、scoped update、MCP indexing は、candidate index の遅延を維持したまま予定された declaration augmentation rebuild に1回だけの graph finalization を担当させます。結果が空でも削除または引き継いだgraph passは確定し、marker検証だけなら全graph走査を省きます。late validation failureではpartial readiness前にfallbackし、`--memory-trace` の graph sample はaugmentation作業後へ帰属させます。TypeScriptのないauthoritative scanはaugmentation rowを再構築せずcontractをstampします。
