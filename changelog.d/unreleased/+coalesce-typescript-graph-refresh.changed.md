---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.Execution.cs
  - src/CodeIndex/Database/DbWriter.TypeScriptAugmentations.cs
---

## English

- **Large TypeScript indexes avoid refreshing the reference graph twice** — CLI full scans, scoped updates, and MCP indexing now let a planned declaration-augmentation rebuild perform the single graph-finalization pass. Empty augmentation results still finalize deletions, late validation failures fall back before partial readiness, and authoritative scans without TypeScript stamp the contract without rebuilding augmentation rows.

## 日本語

- **大規模 TypeScript index で reference graph を2回 refresh しないようになりました** — CLI full scan、scoped update、MCP indexing は、予定された declaration augmentation rebuild に1回だけの graph finalization を担当させます。augmentation 結果が空でも削除を確定し、late validation failure では partial readiness 前に fallback し、TypeScript のない authoritative scan は augmentation row を再構築せず contract を stamp します。
