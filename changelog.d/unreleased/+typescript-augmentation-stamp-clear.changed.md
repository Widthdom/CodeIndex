---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **TypeScript augmentation freshness is cleared with the mutation that needs a rebuild** — CLI and MCP indexing now clear the `typescript_augmentation_version` stamp when a committed file or purge invalidates augmentation references, so interrupted or partial runs force the next successful index to rebuild them instead of trusting an older success stamp.

## 日本語

- **TypeScript augmentation の freshness stamp を再構築が必要な mutation と同時に clear するようにしました** — CLI と MCP の index は、commit 済みファイル更新や purge が augmentation 参照を無効化した時点で `typescript_augmentation_version` を clear し、中断・部分失敗後の次回成功 index が古い成功 stamp を信頼せず必ず再構築するようにしました。
